using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Core.Radio;

namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>Tests <see cref="RadioController"/> orchestration (factory resolution, poll loop, error
/// taxonomy, concurrency contract) against fake <see cref="IRadioProtocol"/>/<see cref="IRadioProtocolFactory"/>
/// implementations -- independent of any real backend or wire protocol.</summary>
public class RadioControllerTests
{
    [Fact]
    public async Task ConnectAsync_ThrowsInvalidOperationException_WhenNoFactoryMatches()
    {
        var controller = new RadioController([], NullLogger<RadioController>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.ConnectAsync(new TestConnectionSpec(), CancellationToken.None));
    }

    [Fact]
    public async Task ConnectAsync_ThrowsInvalidOperationException_WhenMultipleFactoriesMatch()
    {
        var factories = new IRadioProtocolFactory[]
        {
            new FakeProtocolFactory(_ => true, _ => new FakeProtocol(FixedState)),
            new FakeProtocolFactory(_ => true, _ => new FakeProtocol(FixedState)),
        };
        var controller = new RadioController(factories, NullLogger<RadioController>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.ConnectAsync(new TestConnectionSpec(), CancellationToken.None));
    }

    [Fact]
    public async Task ConnectAsync_ThrowsNotSupportedException_ForScanPollingStrategy()
    {
        var factory = new FakeProtocolFactory(_ => true, _ => new FakeProtocol(FixedState));
        var controller = new RadioController([factory], NullLogger<RadioController>.Instance);
        var spec = new TestConnectionSpec { Strategy = PollingStrategy.Scan };

        await Assert.ThrowsAsync<NotSupportedException>(() => controller.ConnectAsync(spec, CancellationToken.None));
    }

    [Fact]
    public async Task ConnectAsync_CancelledToken_PublishesFailed_NotStuckOnConnecting()
    {
        // Regression test for chunk 3b round 2's finding F2: the cancellation check used to sit
        // outside ConnectAsync's own try block, so a cancelled token was the one failure between
        // Connecting and Connected that published no terminal event at all -- every subscriber
        // latched on Connecting forever while only the caller saw the throw.
        var factory = new FakeProtocolFactory(_ => true, _ => new FakeProtocol(FixedState));
        var events = new List<RadioConnectionState>();
        var controller = new RadioController([factory], NullLogger<RadioController>.Instance);
        using var sub = controller.ConnectionEvents.Subscribe(e => events.Add(e.State));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => controller.ConnectAsync(new TestConnectionSpec(), cts.Token));

        Assert.Equal([RadioConnectionState.Connecting, RadioConnectionState.Failed], events);
    }

    [Fact]
    public async Task SetFrequencyAsync_ThrowsInvalidOperationException_WhenNotConnected()
    {
        var controller = new RadioController([], NullLogger<RadioController>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.SetFrequencyAsync(14074000, CancellationToken.None));
    }

    [Fact]
    public async Task NoneConnectionSpec_NeverPublishesState_AndReportsNoCapabilities()
    {
        var controller = new RadioController([new NoneRadioProtocolFactory()], NullLogger<RadioController>.Instance);

        await controller.ConnectAsync(new NoneConnectionSpec(), CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(300)); // longer than the default 250ms poll interval

        Assert.Null(controller.LastKnownState);
        Assert.Equal(RadioCapabilities.None, controller.Capabilities);

        await controller.DisconnectAsync(); // must complete promptly, not hang on the never-returning poll
        await controller.DisposeAsync();
    }

    [Fact]
    public async Task ConnectAsync_PublishesConnectingThenConnected_DisconnectAsync_PublishesDisconnected()
    {
        // Deterministic gate, not a shared race: PollAsync never completes on its own (hangs until
        // DisconnectAsync's own cancellation wins), so the poll loop's own ADDITIONAL Connected
        // publish (on the first genuinely successful poll -- see RadioController.IsGenuinelyConnected)
        // can never race this test's exact 3-event assertion below. That path has its own dedicated
        // tests further down.
        var factory = new FakeProtocolFactory(_ => true, _ => new FakeProtocol(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return FixedStateValue; // unreachable -- Task.Delay throws on cancellation before returning
        }));
        var events = new List<RadioConnectionState>();
        var controller = new RadioController([factory], NullLogger<RadioController>.Instance);
        using var sub = controller.ConnectionEvents.Subscribe(e => events.Add(e.State));

        await controller.ConnectAsync(new TestConnectionSpec(), CancellationToken.None);
        await controller.DisconnectAsync();

        Assert.Equal(
            [RadioConnectionState.Connecting, RadioConnectionState.Connected, RadioConnectionState.Disconnected],
            events);
    }

    [Fact]
    public async Task PollLoop_PublishesStateChanges_AndUpdatesLastKnownState()
    {
        var factory = new FakeProtocolFactory(_ => true, _ => new FakeProtocol(FixedState));
        var received = new List<RadioState>();
        var controller = new RadioController([factory], NullLogger<RadioController>.Instance);
        using var sub = controller.StateChanges.Subscribe(received.Add);

        var spec = new TestConnectionSpec { PollInterval = TimeSpan.FromMilliseconds(20) };
        await controller.ConnectAsync(spec, CancellationToken.None);
        await WaitUntilAsync(() => received.Count >= 3, TimeSpan.FromSeconds(2));

        // Asserted before disconnecting -- DisconnectAsync deliberately clears LastKnownState back to
        // null (spec/02-radio-layer.md: no stale state once nothing is connected), so this must be
        // checked while still connected, not after.
        Assert.True(received.Count >= 3);
        Assert.Equal(14074000, controller.LastKnownState!.Value.FrequencyHz);

        await controller.DisconnectAsync();
        Assert.Null(controller.LastKnownState);
    }

    [Fact]
    public async Task PollLoop_OnRadioProtocolException_PublishesCommandFailed_KeepsPolling_NeverReconnects()
    {
        var pollCount = 0;
        var createCount = 0;
        var factory = new FakeProtocolFactory(_ => true, _ =>
        {
            createCount++;
            return new FakeProtocol(_ =>
            {
                pollCount++;
                if (pollCount == 2)
                {
                    throw new RadioProtocolException("simulated command failure");
                }

                return Task.FromResult(FixedStateValue);
            });
        });

        var events = new List<RadioConnectionEvent>();
        var controller = new RadioController([factory], NullLogger<RadioController>.Instance);
        using var sub = controller.ConnectionEvents.Subscribe(events.Add);

        var spec = new TestConnectionSpec { PollInterval = TimeSpan.FromMilliseconds(20) };
        await controller.ConnectAsync(spec, CancellationToken.None);
        await WaitUntilAsync(() => pollCount >= 4, TimeSpan.FromSeconds(2));
        await controller.DisconnectAsync();

        Assert.Equal(1, createCount); // never disposed/recreated -- a command error is not a transport error
        Assert.Contains(events, e => e.State == RadioConnectionState.CommandFailed);
        Assert.DoesNotContain(events, e => e.State is RadioConnectionState.Reconnecting or RadioConnectionState.Failed);
    }

    [Fact]
    public async Task PollLoop_OnTransportError_DisposesAndRecreatesTheProtocol_AndPublishesReconnecting()
    {
        var createCount = 0;
        var disposeCount = 0;
        var factory = new FakeProtocolFactory(_ => true, _ =>
        {
            createCount++;
            var failThisInstance = createCount == 1;
            return new FakeProtocol(
                _ => failThisInstance
                    ? throw new IOException("simulated transport failure")
                    : Task.FromResult(FixedStateValue),
                onDispose: () => disposeCount++);
        });

        var events = new List<RadioConnectionEvent>();
        var controller = new RadioController([factory], NullLogger<RadioController>.Instance);
        using var sub = controller.ConnectionEvents.Subscribe(events.Add);

        var spec = new TestConnectionSpec { PollInterval = TimeSpan.FromMilliseconds(20) };
        await controller.ConnectAsync(spec, CancellationToken.None);
        await WaitUntilAsync(() => createCount >= 2, TimeSpan.FromSeconds(5));
        await controller.DisconnectAsync();

        Assert.True(createCount >= 2);
        Assert.True(disposeCount >= 1);
        Assert.Contains(events, e => e.State == RadioConnectionState.Reconnecting);
    }

    [Fact]
    public async Task IsGenuinelyConnected_FlipsTrue_AndPublishesASecondConnected_OnFirstSuccessfulPoll()
    {
        var factory = new FakeProtocolFactory(_ => true, _ => new FakeProtocol(FixedState));
        var events = new List<RadioConnectionState>();
        var controller = new RadioController([factory], NullLogger<RadioController>.Instance);
        using var sub = controller.ConnectionEvents.Subscribe(e => events.Add(e.State));

        Assert.False(controller.IsGenuinelyConnected);

        var spec = new TestConnectionSpec { PollInterval = TimeSpan.FromMilliseconds(20) };
        await controller.ConnectAsync(spec, CancellationToken.None);
        await WaitUntilAsync(() => controller.IsGenuinelyConnected, TimeSpan.FromSeconds(2));
        await controller.DisconnectAsync();

        Assert.False(controller.IsGenuinelyConnected); // reset by the disconnect above
        Assert.Equal(
            [RadioConnectionState.Connecting, RadioConnectionState.Connected, RadioConnectionState.Connected, RadioConnectionState.Disconnected],
            events);
    }

    [Fact]
    public async Task CommandFailedPoll_DoesNotConfirmTheLatch()
    {
        // Round-2 plan-review finding: a command-level failure only proves the session is intact, not
        // that a radio is actually attached -- e.g. FlrigClientProtocol.PollAsync's own "no
        // transceiver" case is a CommandFailed that must never read as "genuinely connected."
        var factory = new FakeProtocolFactory(_ => true, _ => new FakeProtocol(
            _ => throw new RadioProtocolException("simulated: rig offline, session intact")));

        var events = new List<RadioConnectionState>();
        var controller = new RadioController([factory], NullLogger<RadioController>.Instance);
        using var sub = controller.ConnectionEvents.Subscribe(e => events.Add(e.State));

        var spec = new TestConnectionSpec { PollInterval = TimeSpan.FromMilliseconds(10) };
        await controller.ConnectAsync(spec, CancellationToken.None);
        await WaitUntilAsync(() => events.Count(e => e == RadioConnectionState.CommandFailed) >= 3, TimeSpan.FromSeconds(2));
        await controller.DisconnectAsync();

        Assert.False(controller.IsGenuinelyConnected);
        Assert.DoesNotContain(events, e => e == RadioConnectionState.Reconnecting || e == RadioConnectionState.Failed);
        // Exactly the one Connected from ConnectAsync itself -- no second one from CommandFailed.
        Assert.Equal(1, events.Count(e => e == RadioConnectionState.Connected));
    }

    [Fact]
    public async Task RecoveryAfterTransportFailure_PublishesAnotherConnected_ClosingTheStuckCatLinkedBug()
    {
        var createCount = 0;
        var factory = new FakeProtocolFactory(_ => true, _ =>
        {
            createCount++;
            var failThisInstance = createCount == 1;
            return new FakeProtocol(
                _ => failThisInstance
                    ? throw new IOException("simulated transport failure")
                    : Task.FromResult(FixedStateValue));
        });

        var events = new List<RadioConnectionState>();
        var controller = new RadioController([factory], NullLogger<RadioController>.Instance);
        using var sub = controller.ConnectionEvents.Subscribe(e => events.Add(e.State));

        var spec = new TestConnectionSpec { PollInterval = TimeSpan.FromMilliseconds(20) };
        await controller.ConnectAsync(spec, CancellationToken.None);
        await WaitUntilAsync(() => controller.IsGenuinelyConnected, TimeSpan.FromSeconds(5));
        await controller.DisconnectAsync();

        // Connecting, Connected (optimistic, from ConnectAsync), Reconnecting (the simulated
        // failure), Connected (genuine, once the recovered poll actually succeeded), Disconnected.
        Assert.Equal(
            [
                RadioConnectionState.Connecting,
                RadioConnectionState.Connected,
                RadioConnectionState.Reconnecting,
                RadioConnectionState.Connected,
                RadioConnectionState.Disconnected,
            ],
            events);
    }

    [Fact]
    public async Task NoneBackend_IsGenuinelyConnectedNeverBecomesTrue()
    {
        var controller = new RadioController([new NoneRadioProtocolFactory()], NullLogger<RadioController>.Instance);

        await controller.ConnectAsync(new NoneConnectionSpec(), CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(300)); // longer than the default 250ms poll interval

        Assert.False(controller.IsGenuinelyConnected);

        await controller.DisconnectAsync();
        await controller.DisposeAsync();
    }

    [Fact]
    public async Task DisconnectAsync_RacingAStragglerSuccessfulPoll_LeavesIsGenuinelyConnectedFalse()
    {
        // Exercises the straggler-vs-DisconnectAsync guard: a poll that's about to confirm the latch
        // is gated to complete only AFTER DisconnectAsync's own cancellation has already been
        // requested -- the guard (re-checking _sessionActive/ct.IsCancellationRequested immediately
        // before publishing) must stop it from publishing a false Connected, or leaving
        // IsGenuinelyConnected true, after Disconnected has already fired.
        var pollGate = new ManualResetEventSlim(initialState: false);
        var factory = new FakeProtocolFactory(_ => true, _ => new FakeProtocol(async ct =>
        {
            // Deterministic gate, not a timing assumption -- blocks the poll right until the test
            // has issued (and given a moment to land) DisconnectAsync's own cancellation.
            await Task.Run(() => pollGate.Wait(TimeSpan.FromSeconds(5)), CancellationToken.None);
            return FixedStateValue;
        }));

        var events = new List<RadioConnectionState>();
        var controller = new RadioController([factory], NullLogger<RadioController>.Instance);
        using var sub = controller.ConnectionEvents.Subscribe(e => events.Add(e.State));

        try
        {
            var spec = new TestConnectionSpec { PollInterval = TimeSpan.FromMilliseconds(5) };
            await controller.ConnectAsync(spec, CancellationToken.None);

            var disconnectTask = controller.DisconnectAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(100)); // let DisconnectAsync's own cts.CancelAsync land
            pollGate.Set();
            await disconnectTask;

            Assert.False(controller.IsGenuinelyConnected);
            // The straggler's own Connected (if it got far enough to publish at all) must never be
            // the last thing observed -- Disconnected must win.
            Assert.Equal(RadioConnectionState.Disconnected, events[^1]);
        }
        finally
        {
            pollGate.Set();
        }
    }

    [Fact]
    public async Task RigId_StaysStable_DuringReconnectBackoff_NotFlappingToNone()
    {
        // Regression test for a code-review finding on spec/18-path-to-1.0.md Critical item 1:
        // RigId used to read straight off the live _protocol field, so it flapped to "none" during
        // the poll loop's protocol-null gap between disposing a failed transport and resolving its
        // replacement -- silently defeating PTT keying for a genuinely configured, momentarily
        // unreachable rig. RadioController now caches the id separately and only clears it on an
        // explicit DisconnectAsync.
        var createCount = 0;
        var factory = new FakeProtocolFactory(_ => true, _ =>
        {
            createCount++;
            var failThisInstance = createCount == 1;
            return new FakeProtocol(
                _ => failThisInstance
                    ? throw new IOException("simulated transport failure")
                    : Task.FromResult(FixedStateValue));
        });

        var controller = new RadioController([factory], NullLogger<RadioController>.Instance);
        var spec = new TestConnectionSpec { PollInterval = TimeSpan.FromMilliseconds(100) };
        await controller.ConnectAsync(spec, CancellationToken.None);

        Assert.Equal("fake", controller.RigId);

        var seenDuringBackoff = new List<string>();
        var seenGenuinelyConnectedDuringBackoff = new List<bool>();
        var sw = Stopwatch.StartNew();
        while (createCount < 2 && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            seenDuringBackoff.Add(controller.RigId);
            seenGenuinelyConnectedDuringBackoff.Add(controller.IsGenuinelyConnected);
            await Task.Delay(5);
        }

        await controller.DisconnectAsync();

        Assert.True(createCount >= 2);
        Assert.NotEmpty(seenDuringBackoff);
        Assert.All(seenDuringBackoff, id => Assert.Equal("fake", id));
        Assert.Equal("none", controller.RigId); // explicit disconnect does reset it
        // RigId's sticky "a session exists" answer and IsGenuinelyConnected's "actually verified"
        // answer are genuinely different questions -- RigId stays "fake" throughout the very backoff
        // window where IsGenuinelyConnected is correctly still false (the first instance's transport
        // failure hasn't been proven recovered from yet).
        Assert.NotEmpty(seenGenuinelyConnectedDuringBackoff);
        Assert.All(seenGenuinelyConnectedDuringBackoff, confirmed => Assert.False(confirmed));
    }

    [Fact]
    public async Task DisconnectAsync_DuringReconnectBackoffNullProtocolWindow_StillTearsDownFully()
    {
        // Regression test for chunk 3b round 1's blocker 1: DisconnectAsync's whole teardown used to be
        // gated on `_protocol is not null`, which is false for the entire reconnect-backoff window
        // (SafeDisposeProtocolAsync nulls it, and it stays null for as long as ResolveProtocol keeps
        // failing/blocking). A disconnect landing there used to skip publishing Disconnected and leave
        // RigId at the stale live value -- forever, since every later DisconnectAsync also saw
        // _protocol == null. RadioController now gates teardown on _sessionActive instead, tracked
        // independently of _protocol's own nullness.
        var createCount = 0;
        var resolveGate = new ManualResetEventSlim(initialState: false);
        var factory = new FakeProtocolFactory(_ => true, _ =>
        {
            var count = Interlocked.Increment(ref createCount);
            if (count == 1)
            {
                return new FakeProtocol(_ => throw new IOException("simulated transport failure"));
            }

            // Deterministic gate, not a timing assumption -- blocks the poll loop's own reconnect
            // attempt right at the point _protocol is still null, so the test asserts against that
            // exact window instead of racing how fast a real reconnect resolves.
            resolveGate.Wait(TimeSpan.FromSeconds(5));
            return new FakeProtocol(FixedState);
        });

        var events = new List<RadioConnectionState>();
        var controller = new RadioController([factory], NullLogger<RadioController>.Instance);
        using var sub = controller.ConnectionEvents.Subscribe(e => events.Add(e.State));

        try
        {
            var spec = new TestConnectionSpec { PollInterval = TimeSpan.FromMilliseconds(5) };
            await controller.ConnectAsync(spec, CancellationToken.None);
            await WaitUntilAsync(() => createCount >= 2, TimeSpan.FromSeconds(2));

            // createCount >= 2 means the reconnect attempt's Create() call is blocked on the gate --
            // _protocol is guaranteed null right now: SafeDisposeProtocolAsync cleared it after the
            // first poll failure, and nothing reassigns it until this blocked Create() call returns.
            var disconnectTask = controller.DisconnectAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(100)); // let DisconnectAsync's own cts.CancelAsync land
            resolveGate.Set();
            await disconnectTask;

            Assert.Contains(RadioConnectionState.Disconnected, events);
            Assert.Equal("none", controller.RigId);
            Assert.Null(controller.LastKnownState);
        }
        finally
        {
            resolveGate.Set();
        }
    }

    [Fact]
    public async Task PollLoop_SurvivesAThrowingStateChangesSubscriber()
    {
        var pollCount = 0;
        var factory = new FakeProtocolFactory(_ => true, _ => new FakeProtocol(_ =>
        {
            pollCount++;
            return Task.FromResult(FixedStateValue);
        }));

        var controller = new RadioController([factory], NullLogger<RadioController>.Instance);
        // Subscribed first -- Subject<T>.OnNext propagates a subscriber's exception synchronously and
        // skips notifying subscribers registered after the one that threw, so this deliberately proves
        // the POLL LOOP survives, not that every subscriber gets notified every time.
        using var badSub = controller.StateChanges.Subscribe(_ => throw new InvalidOperationException("boom"));

        var spec = new TestConnectionSpec { PollInterval = TimeSpan.FromMilliseconds(20) };
        await controller.ConnectAsync(spec, CancellationToken.None);
        await WaitUntilAsync(() => pollCount >= 3, TimeSpan.FromSeconds(2));
        await controller.DisconnectAsync();

        Assert.True(pollCount >= 3);
    }

    [Fact]
    public async Task GivesUpAfter5ConsecutiveTransportFailures_PublishesDisconnectedWithReason_ResetsAllSessionState()
    {
        // Give-up-after-5 feature: a saved backend config pointing at nothing (the user's own
        // reported scenario) used to retry forever. 5 consecutive transport failures with no
        // intervening success/CommandFailed now gives up -- a full Disconnected, not another
        // Reconnecting, and every session field reset the same way an explicit Disconnect would.
        var factory = new FakeProtocolFactory(_ => true, _ => new FakeProtocol(
            _ => throw new IOException("simulated: dead backend")));

        var events = new List<RadioConnectionEvent>();
        var controller = new RadioController([factory], NullLogger<RadioController>.Instance);
        using var sub = controller.ConnectionEvents.Subscribe(events.Add);

        var spec = new TestConnectionSpec { PollInterval = TimeSpan.FromMilliseconds(5) };
        await controller.ConnectAsync(spec, CancellationToken.None);

        await WaitUntilAsync(
            () => events.Any(e => e.State == RadioConnectionState.Disconnected),
            TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(100)); // let any (unexpected) straggler event land

        // 4 Reconnecting -- the 5th failure gives up instead of publishing a 5th one (Design point 1:
        // "skipping that final Reconnecting is safe -- nothing counts them").
        Assert.Equal(4, events.Count(e => e.State == RadioConnectionState.Reconnecting));
        var giveUp = Assert.Single(events, e => e.State == RadioConnectionState.Disconnected);
        Assert.NotNull(giveUp.Reason);
        Assert.Equal(RadioConnectionState.Disconnected, events[^1].State); // terminal

        Assert.Equal("none", controller.RigId);
        Assert.False(controller.IsGenuinelyConnected);
        Assert.Null(controller.LastKnownState);

        await controller.DisconnectAsync(); // must complete promptly -- session already torn down
    }

    [Fact]
    public async Task CommandFailedBetweenTransportFailures_ResetsTheCounter_NeverGivesUp()
    {
        // A connection that's mostly working (one flaky command between transport drops) must never
        // give up, no matter how many TOTAL transport failures accumulate -- CommandFailed resets
        // attempt to 0 (RadioController.cs's own catch block), so only 5 CONSECUTIVE transport
        // failures with no intervening CommandFailed/success ever trigger a give-up.
        var pollCount = 0;
        var factory = new FakeProtocolFactory(_ => true, _ => new FakeProtocol(_ =>
        {
            var count = Interlocked.Increment(ref pollCount);
            if (count % 2 == 1)
            {
                throw new IOException("simulated transport failure");
            }

            throw new RadioProtocolException("simulated command failure");
        }));

        var events = new List<RadioConnectionEvent>();
        var controller = new RadioController([factory], NullLogger<RadioController>.Instance);
        using var sub = controller.ConnectionEvents.Subscribe(events.Add);

        var spec = new TestConnectionSpec { PollInterval = TimeSpan.FromMilliseconds(5) };
        await controller.ConnectAsync(spec, CancellationToken.None);
        await WaitUntilAsync(() => events.Count(e => e.State == RadioConnectionState.Reconnecting) >= 8, TimeSpan.FromSeconds(5));
        await controller.DisconnectAsync();

        Assert.DoesNotContain(events, e => e.State == RadioConnectionState.Disconnected && e.Reason is not null);
    }

    [Fact]
    public async Task RecoversBeforeReachingMaxAttempts_NeverGivesUp()
    {
        var createCount = 0;
        var factory = new FakeProtocolFactory(_ => true, _ =>
        {
            createCount++;
            var failThisInstance = createCount <= 3; // fails attempts 1-3, succeeds from the 4th on
            return new FakeProtocol(
                _ => failThisInstance
                    ? throw new IOException("simulated transport failure")
                    : Task.FromResult(FixedStateValue));
        });

        var events = new List<RadioConnectionEvent>();
        var controller = new RadioController([factory], NullLogger<RadioController>.Instance);
        using var sub = controller.ConnectionEvents.Subscribe(events.Add);

        var spec = new TestConnectionSpec { PollInterval = TimeSpan.FromMilliseconds(20) };
        await controller.ConnectAsync(spec, CancellationToken.None);
        await WaitUntilAsync(() => controller.IsGenuinelyConnected, TimeSpan.FromSeconds(5));
        await controller.DisconnectAsync();

        Assert.DoesNotContain(events, e => e.State == RadioConnectionState.Disconnected && e.Reason is not null);
    }

    [Fact]
    public async Task GiveUp_PublishesDisconnectedBeforeItsOwnUnboundedDispose_NotAfter()
    {
        // Round-2 blocker regression: SafeDisposeProtocolAsync (reused for the give-up path's own
        // dispose) is unbounded. Round 1's ordering (field resets -> dispose -> publish) meant a
        // hung dispose left NOBODY having published Disconnected -- a racing DisconnectAsync would
        // time out its own bounded wait, see _sessionActive already false (the give-up path's own
        // reset), and skip its whole teardown too: the button stuck on "Disconnect" permanently.
        // The fix reorders to publish immediately after the field resets, with no await before it,
        // then dispose last. Two INDEPENDENT gates, not one shared gate plus an assumed order:
        // gating the dispose keyed to any earlier call would block the loop from ever reaching
        // give-up at all, since SafeDisposeProtocolAsync also fires for every backoff-episode
        // dispose along the way -- only the 5th (the give-up path's own) is gated here.
        var disposeCount = 0;
        var giveUpDisposeEntered = new ManualResetEventSlim(initialState: false);
        var giveUpDisposeGate = new ManualResetEventSlim(initialState: false);
        var factory = new FakeProtocolFactory(_ => true, _ => new FakeProtocol(
            _ => throw new IOException("simulated: dead backend"),
            onDispose: () =>
            {
                if (Interlocked.Increment(ref disposeCount) == 5)
                {
                    giveUpDisposeEntered.Set();
                    giveUpDisposeGate.Wait(TimeSpan.FromSeconds(5));
                }
            }));

        var events = new List<RadioConnectionEvent>();
        var controller = new RadioController([factory], NullLogger<RadioController>.Instance);
        using var sub = controller.ConnectionEvents.Subscribe(events.Add);

        try
        {
            var spec = new TestConnectionSpec { PollInterval = TimeSpan.FromMilliseconds(5) };
            await controller.ConnectAsync(spec, CancellationToken.None);

            Assert.True(giveUpDisposeEntered.Wait(TimeSpan.FromSeconds(5)),
                "the give-up branch's own dispose call was never reached");

            // The give-up Disconnected event must already be observable HERE -- while the dispose
            // above is still blocked on the gate. If publish happened after dispose (round 1's
            // order), this would time out instead.
            await WaitUntilAsync(
                () => events.Any(e => e.State == RadioConnectionState.Disconnected && e.Reason is not null),
                TimeSpan.FromSeconds(2));

            Assert.Equal("none", controller.RigId);
            Assert.False(controller.IsGenuinelyConnected);
        }
        finally
        {
            giveUpDisposeGate.Set();
        }

        await controller.DisconnectAsync(); // must complete promptly now that the dispose is unblocked
    }

    private static readonly RadioState FixedStateValue =
        new(14074000, RadioMode.Usb, false, null, DateTimeOffset.UtcNow);

    private static Task<RadioState> FixedState(CancellationToken ct) => Task.FromResult(FixedStateValue);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
            {
                throw new TimeoutException(
                    $"Condition was not met within {timeout} (waited {sw.Elapsed}).");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
    }

    /// <summary>No pre-existing <see cref="RadioConnectionSpec"/> subtype fits a fake-backend test --
    /// demonstrates the design property spec/02-radio-layer.md documents: adding a new subtype needs
    /// no change anywhere in <see cref="RadioController"/>, since it never switches on the concrete
    /// type itself.</summary>
    private sealed record TestConnectionSpec : RadioConnectionSpec;

    private sealed class FakeProtocolFactory(
        Func<RadioConnectionSpec, bool> canHandle,
        Func<RadioConnectionSpec, IRadioProtocol> create) : IRadioProtocolFactory
    {
        public bool CanHandle(RadioConnectionSpec spec) => canHandle(spec);

        public IRadioProtocol Create(RadioConnectionSpec spec) => create(spec);
    }

    private sealed class FakeProtocol(Func<CancellationToken, Task<RadioState>> poll, Action? onDispose = null)
        : IRadioProtocol
    {
        public string RigId => "fake";

        public RadioCapabilities Capabilities => RadioCapabilities.None;

        public Task<RadioState> PollAsync(CancellationToken ct) => poll(ct);

        public Task SetFrequencyAsync(long hz, CancellationToken ct) => Task.CompletedTask;

        public Task SetModeAsync(RadioMode mode, CancellationToken ct) => Task.CompletedTask;

        public Task SetBandwidthAsync(int? bandwidthHz, CancellationToken ct) => Task.CompletedTask;

        public Task SetPttAsync(bool tx, CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            onDispose?.Invoke();
            return ValueTask.CompletedTask;
        }
    }
}
