using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
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
public sealed partial class RadioController : IRadioController, IAsyncDisposable
{
    private const int MaxBackoffAttempt = 30; // 2^30 * any realistic PollInterval already exceeds the
                                               // 30s cap many times over; clamping here is what keeps
                                               // the shift below from ever overflowing.
    private static readonly TimeSpan MaxBackoffDelay = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyList<IRadioProtocolFactory> _factories;
    private readonly ILogger<RadioController> _logger;
    private readonly BehaviorSubject<RadioState?> _stateChanges = new(null);
    private readonly Subject<RadioConnectionEvent> _connectionEvents = new();

    private IRadioProtocol? _protocol;
    private CancellationTokenSource? _pollLoopCts;
    private Task? _pollLoopTask;
    private bool _disposed;

    // Cached separately from _protocol -- see RigId's own doc comment. Set whenever a protocol is
    // freshly resolved (ConnectAsync, and the poll loop's own reconnect-after-backoff path); reset to
    // "none" only on an explicit DisconnectAsync, NEVER by SafeDisposeCurrentProtocolAsync (a
    // transient reconnect-backoff disposal, not a real "no radio configured" state) -- code-review
    // finding on spec/18-path-to-1.0.md Critical item 1: reading RigId straight off _protocol made it
    // flap to "none" during the poll loop's protocol-null window (mid-backoff, before the next
    // reconnect attempt), silently defeating PTT keying for a genuinely configured, momentarily
    // unreachable rig -- the same failure class round-1 plan-review rejected for Capabilities, just
    // relocated. Caching here means a transient drop now surfaces loudly (SetPttAsync throws via
    // RequireProtocol, same as before this whole fix existed) instead of silently skipping.
    private string _rigId = "none";

    // Gates the poll loop's failure logging so a dead rig logs once on entering a failure state,
    // not every poll interval forever (the poll loop is effectively a hot path once backed off to
    // a short interval) -- see docs/logging-guidelines.md's hot-path rule.
    private RadioConnectionState? _lastLoggedFailureState;

    public RadioController(IEnumerable<IRadioProtocolFactory> factories, ILogger<RadioController> logger)
    {
        _factories = factories.ToList();
        _logger = logger;
    }

    public RadioState? LastKnownState => _stateChanges.Value;

    public RadioCapabilities Capabilities => _protocol?.Capabilities ?? RadioCapabilities.None;

    public string RigId => _rigId;

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
        _rigId = _protocol.RigId;

        PublishConnectionEvent(RadioConnectionState.Connected, reason: null, error: null);
        Log.Connected(_logger, spec.GetType().Name, _protocol.Capabilities);
        _lastLoggedFailureState = null;

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
            _rigId = "none";
            await protocol.DisposeAsync().ConfigureAwait(false);
            _stateChanges.OnNext(null);
            PublishConnectionEvent(RadioConnectionState.Disconnected, reason: null, error: null);
            Log.Disconnected(_logger);
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
        var protocol = matches.Count switch
        {
            0 => throw new InvalidOperationException(
                $"No IRadioProtocolFactory is registered for {spec.GetType().Name}."),
            > 1 => throw new InvalidOperationException(
                $"{matches.Count} IRadioProtocolFactory instances all claim to handle " +
                $"{spec.GetType().Name} -- registration is ambiguous, exactly one must match."),
            _ => matches[0].Create(spec),
        };
        Log.ProtocolResolved(_logger, matches[0].GetType().Name, matches.Count);
        return protocol;
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
                // Gated by state transition (not every poll) -- see the hot-path rule this class's
                // own _lastLoggedFailureState field doc comment references.
                if (_lastLoggedFailureState != RadioConnectionState.CommandFailed)
                {
                    _lastLoggedFailureState = RadioConnectionState.CommandFailed;
                    Log.CommandFailed(_logger, ex);
                }

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
                var delay = ComputeBackoffDelay(attempt, spec.PollInterval);
                PublishConnectionEvent(RadioConnectionState.Reconnecting, ex.Message, ex);
                // Log the first failure in full, then only a periodic summary -- a dead rig would
                // otherwise log every retry indefinitely once backed off to a short interval.
                if (attempt == 1)
                {
                    Log.TransportFailureEnteringBackoff(_logger, attempt, delay, ex);
                    _lastLoggedFailureState = RadioConnectionState.Reconnecting;
                }
                else if (attempt % 10 == 0)
                {
                    Log.TransportFailureStillRetrying(_logger, attempt, delay);
                }

                await SafeDisposeCurrentProtocolAsync().ConfigureAwait(false);

                if (!await DelayAsync(delay, ct).ConfigureAwait(false))
                {
                    return;
                }

                try
                {
                    // Constructing a protocol instance here only proves the factory could build the
                    // object -- both real factories connect lazily (a bare `new TcpTransport(...)`),
                    // so this can never throw against a genuinely dead rig and must not be treated as
                    // "reconnected." The real test is the next PollAsync call, below -- that's where
                    // recovery is actually logged (a previous version of this logged a false
                    // "reconnected" here, which also permanently suppressed the real one, since
                    // _lastLoggedFailureState was cleared before the connection was ever proven).
                    _protocol = ResolveProtocol(spec);
                    _rigId = _protocol.RigId;
                }
                catch (Exception reconnectEx)
                {
                    PublishConnectionEvent(RadioConnectionState.Failed, reconnectEx.Message, reconnectEx);
                    if (_lastLoggedFailureState != RadioConnectionState.Failed)
                    {
                        _lastLoggedFailureState = RadioConnectionState.Failed;
                        Log.ReconnectAttemptFailed(_logger, attempt, reconnectEx);
                    }
                }

                continue;
            }

            // The real recovery signal -- a poll that actually succeeded, not just a protocol object
            // that constructed. See the comment at ResolveProtocol's call site above for why logging
            // "reconnected" there instead would be a false positive.
            if (_lastLoggedFailureState is not null)
            {
                Log.ReconnectSucceeded(_logger, attempt);
                _lastLoggedFailureState = null;
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
            catch (Exception ex)
            {
                // Disposing an already-broken transport can itself throw -- the protocol is being
                // discarded either way, so there's nothing further to do with this exception beyond
                // recording it for diagnosis.
                Log.DisposeCurrentProtocolFailed(_logger, ex);
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
        catch (Exception ex)
        {
            // A StateChanges subscriber's OnNext threw. Subject<T> rethrows into the caller (this poll
            // loop) and skips notifying any subscriber registered after the one that threw -- per
            // IRadioController's own concurrency contract, an unhandled subscriber exception must never
            // kill the loop, so it's swallowed here after having been attempted once. This is always a
            // subscriber bug, never expected in normal operation -- logged at Error, not Warning.
            Log.StateChangesSubscriberThrew(_logger, ex);
        }
    }

    private void PublishConnectionEvent(RadioConnectionState connState, string? reason, Exception? error)
    {
        var evt = new RadioConnectionEvent(connState, reason, error, DateTimeOffset.UtcNow);
        try
        {
            _connectionEvents.OnNext(evt);
        }
        catch (Exception ex)
        {
            // Same reasoning as PublishState -- must never propagate into ConnectAsync/DisconnectAsync
            // or the poll loop.
            Log.ConnectionEventsSubscriberThrew(_logger, ex);
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
        Log.Disposed(_logger);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Radio connected: spec={SpecType}, capabilities={Capabilities}")]
        public static partial void Connected(ILogger logger, string specType, RadioCapabilities capabilities);

        [LoggerMessage(Level = LogLevel.Information, Message = "Radio disconnected")]
        public static partial void Disconnected(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Resolved protocol via {FactoryType} ({MatchCount} factory match(es))")]
        public static partial void ProtocolResolved(ILogger logger, string factoryType, int matchCount);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Radio command-level poll failure")]
        public static partial void CommandFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Radio transport failure, entering backoff (attempt={Attempt}, delay={Delay})")]
        public static partial void TransportFailureEnteringBackoff(ILogger logger, int attempt, TimeSpan delay, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Radio still retrying after {Attempt} attempts (delay={Delay})")]
        public static partial void TransportFailureStillRetrying(ILogger logger, int attempt, TimeSpan delay);

        [LoggerMessage(Level = LogLevel.Information, Message = "Radio reconnected after {Attempt} attempt(s)")]
        public static partial void ReconnectSucceeded(ILogger logger, int attempt);

        [LoggerMessage(Level = LogLevel.Error, Message = "Radio reconnect attempt {Attempt} failed")]
        public static partial void ReconnectAttemptFailed(ILogger logger, int attempt, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Disposing the current (broken) protocol instance threw")]
        public static partial void DisposeCurrentProtocolFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "A StateChanges subscriber threw")]
        public static partial void StateChangesSubscriberThrew(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "A ConnectionEvents subscriber threw")]
        public static partial void ConnectionEventsSubscriberThrew(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "RadioController disposed")]
        public static partial void Disposed(ILogger logger);
    }
}
