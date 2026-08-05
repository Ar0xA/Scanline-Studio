using System.Reactive.Linq;
using System.Reactive.Subjects;
using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Radio;

/// <summary>
/// See spec/02-radio-layer.md. Reference <see cref="IRadioController"/> implementation: resolves a
/// <see cref="RadioConnectionSpec"/> to a concrete <see cref="IRadioProtocol"/> via the registered
/// <see cref="IRadioProtocolFactory"/> instances (exactly one match required), runs a background poll
/// loop, and fans out <see cref="StateChanges"/>/<see cref="ConnectionEvents"/> per the concurrency
/// contract documented on <see cref="IRadioController"/> itself.
///
/// Error taxonomy (spec/04-rigctld.md): a <see cref="RadioProtocolException"/> from
/// <see cref="IRadioProtocol.PollAsync"/> is a command-level failure — the connection is fine, one poll
/// failed — surfaced as <see cref="RadioConnectionState.CommandFailed"/>, polling continues at normal
/// cadence, backoff is not affected. Any other exception is treated as a transport-level failure: the
/// current protocol is disposed and re-created from scratch via the factory (closing/reopening its
/// transport and re-running capability negotiation), gated by an exponential backoff
/// (<c>min(2^attempt * PollInterval, 30s)</c>, <c>attempt</c> clamped before the shift so it can never
/// overflow, no jitter -- single client, single local daemon, nothing to de-synchronize).
/// </summary>
public sealed class RadioController : IRadioController, IAsyncDisposable
{
    private const int MaxBackoffAttempt = 30; // 2^30 * any realistic PollInterval already exceeds the
                                               // 30s cap many times over; clamping here is what keeps
                                               // the shift below from ever overflowing.
    private static readonly TimeSpan MaxBackoffDelay = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyList<IRadioProtocolFactory> _factories;
    private readonly BehaviorSubject<RadioState?> _stateChanges = new(null);
    private readonly Subject<RadioConnectionEvent> _connectionEvents = new();

    private IRadioProtocol? _protocol;
    private CancellationTokenSource? _pollLoopCts;
    private Task? _pollLoopTask;
    private bool _disposed;

    public RadioController(IEnumerable<IRadioProtocolFactory> factories)
    {
        _factories = factories.ToList();
    }

    public RadioState? LastKnownState => _stateChanges.Value;

    public RadioCapabilities Capabilities => _protocol?.Capabilities ?? RadioCapabilities.None;

    /// <summary>Filters out the internal <c>BehaviorSubject&lt;RadioState?&gt;</c>'s null sentinel
    /// (used to represent "never polled yet"/"disconnected" for <see cref="LastKnownState"/>) --
    /// subscribers here should only ever see real snapshots.</summary>
    public IObservable<RadioState> StateChanges =>
        _stateChanges.Where(s => s.HasValue).Select(s => s!.Value);

    public IObservable<RadioConnectionEvent> ConnectionEvents => _connectionEvents;

    public async Task ConnectAsync(RadioConnectionSpec spec, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(spec);

        if (spec.Strategy == PollingStrategy.Scan)
        {
            throw new NotSupportedException(
                "PollingStrategy.Scan is not implemented by this RadioController reference implementation.");
        }

        await DisconnectAsync().ConfigureAwait(false);

        PublishConnectionEvent(RadioConnectionState.Connecting, reason: null, error: null);

        _protocol = ResolveProtocol(spec);

        PublishConnectionEvent(RadioConnectionState.Connected, reason: null, error: null);

        _pollLoopCts = new CancellationTokenSource();
        // Capture the token into a local before scheduling: Task.Run's lambda body executes later, on
        // a thread-pool thread, so reading the _pollLoopCts field directly inside it would race a
        // concurrent DisconnectAsync that nulls the field before the lambda actually runs --
        // NullReferenceException, caught by a test that connects and immediately disconnects.
        var loopCt = _pollLoopCts.Token;
        _pollLoopTask = Task.Run(() => RunPollLoopAsync(spec, loopCt), CancellationToken.None);
    }

    public async Task DisconnectAsync()
    {
        if (_pollLoopCts is not null)
        {
            var cts = _pollLoopCts;
            var loopTask = _pollLoopTask;
            _pollLoopCts = null;
            _pollLoopTask = null;

            await cts.CancelAsync().ConfigureAwait(false);
            if (loopTask is not null)
            {
                await loopTask.ConfigureAwait(false);
            }

            cts.Dispose();
        }

        if (_protocol is not null)
        {
            var protocol = _protocol;
            _protocol = null;
            await protocol.DisposeAsync().ConfigureAwait(false);
            _stateChanges.OnNext(null);
            PublishConnectionEvent(RadioConnectionState.Disconnected, reason: null, error: null);
        }
    }

    public Task SetFrequencyAsync(long hz, CancellationToken ct) =>
        RequireProtocol().SetFrequencyAsync(hz, ct);

    public Task SetModeAsync(RadioMode mode, CancellationToken ct) =>
        RequireProtocol().SetModeAsync(mode, ct);

    public Task SetPttAsync(bool tx, CancellationToken ct) =>
        RequireProtocol().SetPttAsync(tx, ct);

    private IRadioProtocol RequireProtocol()
    {
        return _protocol ?? throw new InvalidOperationException(
            "No radio connected -- call ConnectAsync first.");
    }

    private IRadioProtocol ResolveProtocol(RadioConnectionSpec spec)
    {
        var matches = _factories.Where(f => f.CanHandle(spec)).ToList();
        return matches.Count switch
        {
            0 => throw new InvalidOperationException(
                $"No IRadioProtocolFactory is registered for {spec.GetType().Name}."),
            > 1 => throw new InvalidOperationException(
                $"{matches.Count} IRadioProtocolFactory instances all claim to handle " +
                $"{spec.GetType().Name} -- registration is ambiguous, exactly one must match."),
            _ => matches[0].Create(spec),
        };
    }

    private async Task RunPollLoopAsync(RadioConnectionSpec spec, CancellationToken ct)
    {
        var attempt = 0;

        while (true)
        {
            RadioState state;
            try
            {
                var protocol = _protocol ?? throw new InvalidOperationException(
                    "Poll loop has no active protocol -- this indicates a reconnect left the " +
                    "controller in an inconsistent state.");
                state = await protocol.PollAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (RadioProtocolException ex)
            {
                // Command-level failure -- the connection is fine. Don't touch backoff, keep cadence.
                attempt = 0;
                PublishConnectionEvent(RadioConnectionState.CommandFailed, ex.Message, ex);
                if (!await DelayAsync(spec.PollInterval, ct).ConfigureAwait(false))
                {
                    return;
                }

                continue;
            }
            catch (Exception ex)
            {
                // Transport-level failure -- back off, then close/reopen via a fresh protocol instance
                // from the factory (never just retry the same dead transport forever).
                attempt = Math.Min(attempt + 1, MaxBackoffAttempt);
                PublishConnectionEvent(RadioConnectionState.Reconnecting, ex.Message, ex);

                await SafeDisposeCurrentProtocolAsync().ConfigureAwait(false);

                if (!await DelayAsync(ComputeBackoffDelay(attempt, spec.PollInterval), ct).ConfigureAwait(false))
                {
                    return;
                }

                try
                {
                    _protocol = ResolveProtocol(spec);
                }
                catch (Exception reconnectEx)
                {
                    PublishConnectionEvent(RadioConnectionState.Failed, reconnectEx.Message, reconnectEx);
                }

                continue;
            }

            attempt = 0;
            PublishState(state);

            if (!await DelayAsync(spec.PollInterval, ct).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private async Task SafeDisposeCurrentProtocolAsync()
    {
        var protocol = _protocol;
        _protocol = null;
        if (protocol is not null)
        {
            try
            {
                await protocol.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Disposing an already-broken transport can itself throw -- the protocol is being
                // discarded either way, so there's nothing further to do with this exception.
            }
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static TimeSpan ComputeBackoffDelay(int attempt, TimeSpan pollInterval)
    {
        var multiplier = 1L << attempt; // safe: attempt is always <= MaxBackoffAttempt (30) by the time
                                         // this is called, so the shift never overflows a long.
        var delayMs = Math.Min(pollInterval.TotalMilliseconds * multiplier, MaxBackoffDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(delayMs);
    }

    private void PublishState(RadioState state)
    {
        try
        {
            _stateChanges.OnNext(state);
        }
        catch
        {
            // A StateChanges subscriber's OnNext threw. Subject<T> rethrows into the caller (this poll
            // loop) and skips notifying any subscriber registered after the one that threw -- per
            // IRadioController's own concurrency contract, an unhandled subscriber exception must never
            // kill the loop, so it's swallowed here after having been attempted once.
        }
    }

    private void PublishConnectionEvent(RadioConnectionState connState, string? reason, Exception? error)
    {
        var evt = new RadioConnectionEvent(connState, reason, error, DateTimeOffset.UtcNow);
        try
        {
            _connectionEvents.OnNext(evt);
        }
        catch
        {
            // Same reasoning as PublishState -- must never propagate into ConnectAsync/DisconnectAsync
            // or the poll loop.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisconnectAsync().ConfigureAwait(false);
        _stateChanges.OnCompleted();
        _stateChanges.Dispose();
        _connectionEvents.OnCompleted();
        _connectionEvents.Dispose();
    }
}
