using System.Diagnostics;
using Yoniq.Abstractions.Radio;
using Yoniq.Core.Radio;

namespace Yoniq.Core.Radio.Tests;

/// <summary>Tests <see cref="RadioController"/> orchestration (factory resolution, poll loop, error
/// taxonomy, concurrency contract) against fake <see cref="IRadioProtocol"/>/<see cref="IRadioProtocolFactory"/>
/// implementations -- independent of any real backend or wire protocol.</summary>
public class RadioControllerTests
{
    [Fact]
    public async Task ConnectAsync_ThrowsInvalidOperationException_WhenNoFactoryMatches()
    {
        var controller = new RadioController([]);

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
        var controller = new RadioController(factories);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.ConnectAsync(new TestConnectionSpec(), CancellationToken.None));
    }

    [Fact]
    public async Task ConnectAsync_ThrowsNotSupportedException_ForScanPollingStrategy()
    {
        var factory = new FakeProtocolFactory(_ => true, _ => new FakeProtocol(FixedState));
        var controller = new RadioController([factory]);
        var spec = new TestConnectionSpec { Strategy = PollingStrategy.Scan };

        await Assert.ThrowsAsync<NotSupportedException>(() => controller.ConnectAsync(spec, CancellationToken.None));
    }

    [Fact]
    public async Task SetFrequencyAsync_ThrowsInvalidOperationException_WhenNotConnected()
    {
        var controller = new RadioController([]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.SetFrequencyAsync(14074000, CancellationToken.None));
    }

    [Fact]
    public async Task NoneConnectionSpec_NeverPublishesState_AndReportsNoCapabilities()
    {
        var controller = new RadioController([new NoneRadioProtocolFactory()]);

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
        var factory = new FakeProtocolFactory(_ => true, _ => new FakeProtocol(FixedState));
        var events = new List<RadioConnectionState>();
        var controller = new RadioController([factory]);
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
        var controller = new RadioController([factory]);
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
        var controller = new RadioController([factory]);
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
        var controller = new RadioController([factory]);
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
    public async Task PollLoop_SurvivesAThrowingStateChangesSubscriber()
    {
        var pollCount = 0;
        var factory = new FakeProtocolFactory(_ => true, _ => new FakeProtocol(_ =>
        {
            pollCount++;
            return Task.FromResult(FixedStateValue);
        }));

        var controller = new RadioController([factory]);
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

        public Task SetPttAsync(bool tx, CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            onDispose?.Invoke();
            return ValueTask.CompletedTask;
        }
    }
}
