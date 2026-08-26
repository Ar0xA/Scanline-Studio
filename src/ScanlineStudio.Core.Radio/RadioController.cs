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

    // User-reported gap: a saved backend config pointing at nothing (e.g. a stale rigctld host/port
    // with no daemon running) retried forever with no operator-visible signal beyond the passive
    // status line. 5 consecutive transport-level failures with no intervening success or
    // CommandFailed (both reset the counter -- see RunPollLoopAsync's own catch blocks) now gives up:
    // a full disconnect, not another backoff round.
    private const int MaxConnectAttempts = 5;

    private readonly IReadOnlyList<IRadioProtocolFactory> _factories;
    private readonly ILogger<RadioController> _logger;
    private readonly BehaviorSubject<RadioState?> _stateChanges = new(null);
    private readonly Subject<RadioConnectionEvent> _connectionEvents = new();

    // A backend is not obliged to abandon an in-flight call on cancellation -- HamlibRadioProtocol's
    // own doc comment states cancellation is honored only at its semaphore boundary, and a poll makes
    // up to 6 blocking native calls. Without a bound, DisconnectAsync (and therefore DisposeAsync, and
    // therefore app shutdown) blocks for as long as a wedged CAT device takes to give up. Abandoning
    // the loop is safe only because SafeDisposeProtocolAsync/the reconnect path both claim _protocol
    // by CompareExchange, so a straggler can no longer clobber a later session.
    private static readonly TimeSpan PollLoopShutdownTimeout = TimeSpan.FromSeconds(10);

    // Bounded for the same reason PollLoopShutdownTimeout is: HamlibRadioProtocol.DisposeAsync takes
    // its own semaphore with no token and no timeout, and that same semaphore is held for the whole
    // duration of a wedged native call -- so an unbounded await here would re-introduce, one line after
    // PollLoopShutdownTimeout's own wait, precisely the shutdown hang that timeout exists to prevent.
    // Abandoning the dispose leaks a backend handle until process exit; hanging app shutdown forever
    // (DisconnectAsync -> DisposeAsync -> app quit) is worse.
    private static readonly TimeSpan ProtocolDisposeTimeout = TimeSpan.FromSeconds(10);

    private IRadioProtocol? _protocol;
    private CancellationTokenSource? _pollLoopCts;
    private Task? _pollLoopTask;
    private bool _disposed;

    // True from the moment ConnectAsync successfully resolves a protocol until DisconnectAsync tears
    // the session down -- deliberately NOT derived from `_protocol is not null`. The poll loop's
    // reconnect-backoff window legitimately holds _protocol == null for up to 30s at a time (and
    // indefinitely, if the factory itself keeps throwing), and a DisconnectAsync landing in that
    // window used to skip the ENTIRE teardown block below: _rigId stayed at the live rig's id, no
    // Disconnected event was published, and LastKnownState kept its last stale snapshot -- forever,
    // since every later DisconnectAsync sees _protocol == null too. Same failure class as the _rigId
    // caching fix above, just its mirror image.
    //
    // volatile: read cross-thread by the poll loop's own straggler-vs-DisconnectAsync guard on the
    // IsGenuinelyConnected publish path below (RunPollLoopAsync historically only ever wrote this,
    // never read it -- that read is new, added alongside _connectionConfirmed below).
    private volatile bool _sessionActive;

    // Cached separately from _protocol -- see RigId's own doc comment. Set whenever a protocol is
    // freshly resolved (ConnectAsync, and the poll loop's own reconnect-after-backoff path); reset to
    // "none" only on an explicit DisconnectAsync, NEVER by SafeDisposeProtocolAsync (a
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

    // Distinct from _rigId: _rigId goes non-"none" the instant ConnectAsync resolves a protocol
    // OBJECT (before any real I/O -- both real factories connect lazily), so it answers "is there a
    // session to disconnect," not "have we actually verified this rig is reachable." This latch
    // answers the second question. False on a fresh ConnectAsync and on every transport-level
    // failure (Reconnecting/Failed) -- a backoff episode must not let a stale "yes" survive. Set true,
    // and IsGenuinelyConnected's own additional Connected event published, ONLY when the poll loop's
    // PollAsync call genuinely returns a RadioState -- deliberately NOT on a CommandFailed poll (a
    // command-level failure only proves the session is intact, not that a radio is actually there --
    // e.g. FlrigClientProtocol.PollAsync's own "no transceiver attached" case is a CommandFailed that
    // must never read as "genuinely connected"). volatile: read from arbitrary threads via
    // IsGenuinelyConnected (e.g. a ViewModel at construction), written from the poll-loop thread.
    private volatile bool _connectionConfirmed;

    public RadioController(IEnumerable<IRadioProtocolFactory> factories, ILogger<RadioController> logger)
    {
        _factories = factories.ToList();
        _logger = logger;
    }

    public RadioState? LastKnownState => _stateChanges.Value;

    public RadioCapabilities Capabilities => _protocol?.Capabilities ?? RadioCapabilities.None;

    public string RigId => _rigId;

    public bool IsGenuinelyConnected => _connectionConfirmed;

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

        IRadioProtocol resolved;
        try
        {
            // Inside the try, not above it: this used to sit outside, so a cancelled token was the one
            // failure between Connecting and Connected that published no terminal event at all --
            // every subscriber latched on Connecting forever while only the caller saw the throw.
            ct.ThrowIfCancellationRequested();

            resolved = ResolveProtocol(spec);
            _protocol = resolved;
            // Set BEFORE reading RigId: everything from here on is reachable by DisconnectAsync's
            // (_sessionActive-gated) teardown, so a throw out of a backend's own RigId getter disposes
            // the protocol instead of orphaning it. _protocol non-null with _sessionActive false is
            // exactly the state that teardown skips.
            _sessionActive = true;
            _rigId = resolved.RigId;
        }
        catch (Exception ex)
        {
            // Connecting was already published above -- without this, a cancelled token or a resolution
            // failure leaves every subscriber latched on Connecting forever while only the caller sees
            // the throw.
            PublishConnectionEvent(RadioConnectionState.Failed, ex.Message, ex);
            // A no-op unless _sessionActive was already set (the RigId-getter-throws case) -- the
            // ct-cancelled and ResolveProtocol-throws cases have no session to tear down yet.
            await DisconnectAsync().ConfigureAwait(false);
            throw;
        }

        // Code-review nit: reset BEFORE the publish below, matching this file's own write-then-publish
        // discipline at every other _connectionConfirmed site (benign either order here -- a fresh
        // session's latch is already false whenever this line is reached -- but consistency removes
        // the need to reason about why THIS site is the one exception).
        _connectionConfirmed = false;
        PublishConnectionEvent(RadioConnectionState.Connected, reason: null, error: null);
        // Logs the local just resolved, not _protocol: a ConnectionEvents subscriber that reacts to
        // Connected by synchronously calling DisconnectAsync (nothing prevents that -- Subject<T>.OnNext
        // runs subscribers inline) would null _protocol before this line's first await, turning a field
        // read here into a NullReferenceException.
        Log.Connected(_logger, spec.GetType().Name, resolved.Capabilities);
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

            try
            {
                await cts.CancelAsync().ConfigureAwait(false);
                if (loopTask is not null)
                {
                    try
                    {
                        await loopTask.WaitAsync(PollLoopShutdownTimeout).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        Log.PollLoopShutdownTimedOut(_logger, PollLoopShutdownTimeout);
                    }
                }
            }
            catch (Exception ex)
            {
                // The poll loop task itself faulted. It is written not to, but a throwing ILogger
                // inside one of its own catch blocks still gets one out. Swallowing here is what
                // keeps the teardown below reachable: rethrowing left _protocol non-null and
                // undisposed, _rigId stale and the CTS leaked -- and, because ConnectAsync awaits
                // this method first, made every subsequent connect throw too, permanently wedging
                // the controller.
                Log.PollLoopFaulted(_logger, ex);
            }
            finally
            {
                cts.Dispose();
            }
        }

        if (_sessionActive)
        {
            var protocol = _protocol;
            _protocol = null;
            _rigId = "none";
            _connectionConfirmed = false;
            _sessionActive = false;

            if (protocol is not null)
            {
                try
                {
                    await protocol.DisposeAsync().AsTask()
                        .WaitAsync(ProtocolDisposeTimeout).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    Log.ProtocolDisposeTimedOut(_logger, ProtocolDisposeTimeout);
                }
                catch (Exception ex)
                {
                    // A broken backend's own DisposeAsync must not abort the rest of the teardown --
                    // same reasoning SafeDisposeProtocolAsync already applies on the poll loop's path.
                    Log.DisposeCurrentProtocolFailed(_logger, ex);
                }
            }

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

    public Task SetBandwidthAsync(int? bandwidthHz, CancellationToken ct) =>
        RequireProtocol().SetBandwidthAsync(bandwidthHz, ct);

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
            IRadioProtocol? protocol = null;
            RadioState state;
            try
            {
                protocol = _protocol ?? throw new InvalidOperationException(
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
                if (ct.IsCancellationRequested)
                {
                    // An abandoned loop (PollLoopShutdownTimeout expired while a call was wedged) must
                    // not publish a command-level failure after DisconnectAsync already published
                    // Disconnected -- there is no session left for this event to describe.
                    return;
                }

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
                if (ct.IsCancellationRequested)
                {
                    // Same reasoning as the RadioProtocolException guard above: an abandoned loop must
                    // not publish Reconnecting/Failed or start a fresh reconnect attempt after
                    // DisconnectAsync's teardown already ran.
                    if (protocol is not null)
                    {
                        await SafeDisposeProtocolAsync(protocol).ConfigureAwait(false);
                    }

                    return;
                }

                // Transport-level failure -- back off, then close/reopen via a fresh protocol instance
                // from the factory (never just retry the same dead transport forever).
                attempt = Math.Min(attempt + 1, MaxBackoffAttempt);

                if (attempt >= MaxConnectAttempts)
                {
                    if (ct.IsCancellationRequested)
                    {
                        // Abandoned loop (DisconnectAsync's own bounded wait already timed out and
                        // moved on, or this loop was cancelled for some other reason) -- whatever
                        // superseded this session owns its own teardown; touching shared state here
                        // would race it, same reasoning as the other guarded exits in this loop.
                        if (protocol is not null)
                        {
                            await SafeDisposeProtocolAsync(protocol).ConfigureAwait(false);
                        }

                        return;
                    }

                    _rigId = "none";
                    _connectionConfirmed = false;
                    _sessionActive = false;
                    _stateChanges.OnNext(null);

                    // Log + publish BEFORE the dispose below, with NO await in between:
                    // SafeDisposeProtocolAsync is unbounded (unlike DisconnectAsync's own
                    // ProtocolDisposeTimeout-wrapped dispose), so a racing DisconnectAsync could time
                    // out waiting for this loop task, see _sessionActive already false, and skip its
                    // own teardown entirely while this dispose is still stuck -- nobody would ever
                    // publish Disconnected. Publishing first (not after, unlike DisconnectAsync's own
                    // order) closes that window structurally, not just by a second cancellation check.
                    Log.GaveUp(_logger, MaxConnectAttempts, ex);
                    PublishConnectionEvent(RadioConnectionState.Disconnected,
                        $"Gave up after {MaxConnectAttempts} attempts: {ex.Message}", ex);

                    if (protocol is not null)
                    {
                        await SafeDisposeProtocolAsync(protocol).ConfigureAwait(false);
                    }

                    return;
                }

                var delay = ComputeBackoffDelay(attempt, spec.PollInterval);
                // Must not survive a backoff episode -- a stale "confirmed" would let
                // IsGenuinelyConnected keep reporting true for a link that just genuinely broke.
                _connectionConfirmed = false;
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

                if (protocol is not null)
                {
                    await SafeDisposeProtocolAsync(protocol).ConfigureAwait(false);
                }

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
                    var fresh = ResolveProtocol(spec);

                    // Only claim the slot if it is still the empty one this iteration vacated. A
                    // DisconnectAsync (or a second ConnectAsync) racing this window has already
                    // installed its own value -- or deliberately left it null -- and blindly
                    // overwriting it would resurrect a session the caller just tore down, or orphan a
                    // freshly-connected protocol nobody would ever dispose.
                    if (Interlocked.CompareExchange(ref _protocol, fresh, null) is not null)
                    {
                        await SafeDisposeProtocolAsync(fresh).ConfigureAwait(false);
                        return;
                    }

                    // Re-checked AFTER claiming the slot, not before: a DisconnectAsync whose bounded
                    // wait timed out can run its whole teardown in the window between an earlier check
                    // and this CAS, leaving _sessionActive false with `fresh` now installed -- a live,
                    // undisposed protocol on a torn-down controller. SafeDisposeProtocolAsync's own CAS
                    // is instance-matched, so un-installing it here is safe even if a newer session has
                    // since claimed the slot.
                    if (ct.IsCancellationRequested)
                    {
                        await SafeDisposeProtocolAsync(fresh).ConfigureAwait(false);
                        return;
                    }

                    _rigId = fresh.RigId;
                }
                catch (Exception reconnectEx)
                {
                    if (ct.IsCancellationRequested)
                    {
                        // Fourth publish site in this loop, guarded for the same reason the other
                        // three are: an abandoned loop that resumes after DisconnectAsync's teardown
                        // already published Disconnected must not publish a terminal Failed afterward
                        // -- nothing publishes again until the next real connect, so every subscriber
                        // would latch on it permanently.
                        return;
                    }

                    PublishConnectionEvent(RadioConnectionState.Failed, reconnectEx.Message, reconnectEx);
                    if (_lastLoggedFailureState != RadioConnectionState.Failed)
                    {
                        _lastLoggedFailureState = RadioConnectionState.Failed;
                        Log.ReconnectAttemptFailed(_logger, attempt, reconnectEx);
                    }
                }

                continue;
            }

            // Checked before any of this iteration's own logging/publishing, not after: an abandoned
            // loop that resumes here (PollLoopShutdownTimeout expired while a call was wedged) must not
            // log a false "reconnected" or re-publish a snapshot after DisconnectAsync's teardown
            // already published Disconnected -- LastKnownState would otherwise read non-null for a
            // controller with no session, permanently (nothing clears it again until the next connect).
            if (ct.IsCancellationRequested)
            {
                return;
            }

            // The real recovery signal -- a poll that actually succeeded, not just a protocol object
            // that constructed. See the comment at ResolveProtocol's call site above for why logging
            // "reconnected" there instead would be a false positive.
            if (_lastLoggedFailureState is not null)
            {
                Log.ReconnectSucceeded(_logger, attempt);
                _lastLoggedFailureState = null;
            }

            // Publishes an ADDITIONAL Connected event, on top of ConnectAsync's own (unconditional,
            // fires immediately on resolve -- left untouched, IsRadioConnected depends on it) -- this
            // one only on the false->true transition, so consumers that need to distinguish "resolved"
            // from "genuinely verified" (RadioStatusViewModel.CatLinked) have something to react to,
            // including after a real recovery from Reconnecting/Failed, which otherwise publishes
            // nothing at all once the poll starts succeeding again. Re-checks _sessionActive and
            // ct.IsCancellationRequested immediately before publishing -- an abandoned straggler
            // iteration (PollLoopShutdownTimeout expired) must not publish a false Connected after
            // DisconnectAsync's own teardown already published Disconnected; same window
            // PublishState below already accepts, not a new or wider one.
            // Code-review nit: the latch write sits INSIDE the guard, not before it -- an abandoned
            // straggler that fails the guard must not leave _connectionConfirmed true with no
            // published event to justify it (structurally unreachable today given DisconnectAsync's
            // own teardown order, but keeping the write and the publish it justifies atomic costs
            // nothing and removes the need to reason about why it was safe).
            if (!_connectionConfirmed && _sessionActive && !ct.IsCancellationRequested)
            {
                _connectionConfirmed = true;
                PublishConnectionEvent(RadioConnectionState.Connected, reason: null, error: null);
            }

            attempt = 0;
            PublishState(state);

            if (!await DelayAsync(spec.PollInterval, ct).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private async Task SafeDisposeProtocolAsync(IRadioProtocol protocol)
    {
        // CompareExchange, not a bare `_protocol = null`: only clear the field if it still points at
        // the instance being disposed. An abandoned or racing loop iteration must never null out a
        // protocol a concurrent ConnectAsync has since installed.
        Interlocked.CompareExchange(ref _protocol, null, protocol);
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

        [LoggerMessage(Level = LogLevel.Error, Message = "Radio gave up after {Attempt} consecutive failed connection attempts")]
        public static partial void GaveUp(ILogger logger, int attempt, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Disposing the current (broken) protocol instance threw")]
        public static partial void DisposeCurrentProtocolFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "The radio poll loop faulted; teardown continued regardless")]
        public static partial void PollLoopFaulted(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "The radio poll loop did not stop within {Timeout}; abandoning it and completing teardown")]
        public static partial void PollLoopShutdownTimedOut(ILogger logger, TimeSpan timeout);

        [LoggerMessage(Level = LogLevel.Error, Message = "Disposing the current protocol did not complete within {Timeout}; abandoning it and completing teardown")]
        public static partial void ProtocolDisposeTimedOut(ILogger logger, TimeSpan timeout);

        [LoggerMessage(Level = LogLevel.Error, Message = "A StateChanges subscriber threw")]
        public static partial void StateChangesSubscriberThrew(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "A ConnectionEvents subscriber threw")]
        public static partial void ConnectionEventsSubscriberThrew(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "RadioController disposed")]
        public static partial void Disposed(ILogger logger);
    }
}
