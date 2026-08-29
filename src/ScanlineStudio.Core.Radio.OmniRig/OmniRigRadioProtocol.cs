using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Radio.OmniRig;

/// <summary>See spec/03-cat-layer.md's OmniRig section and the implementation plan.
/// <see cref="IRadioProtocol"/> for OmniRig's <c>Rig1</c> automation object, via
/// <see cref="IOmniRigComClient"/>. Mirrors <c>FlrigClientProtocol</c>'s shape (request-lock
/// serialization, sticky-once-confirmed <see cref="Capabilities"/>) -- the closer structural analog
/// is Hamlib's <c>IHamlibNative</c> seam split, since both this backend and Hamlib are call-based
/// rather than byte-stream.
///
/// <b>The per-transaction/connect timeouts are best-effort deadlines, not preemptive
/// cancellation</b> (round-2 code-review finding): <c>IOmniRigComClient</c>'s real implementation
/// runs each COM call via <c>Task.Run</c>, whose <see cref="CancellationToken"/> is only honored
/// *before* the delegate starts, never once it is running -- once a real COM call is actually in
/// flight, a hung OmniRig blocks the calling operation until it returns, exactly like Hamlib's own
/// documented "once a native call starts, it is never cancelled or abandoned" contract
/// (<c>HamlibRadioProtocol.cs</c>). The timeout still bounds an operation that hasn't started yet
/// (e.g. queued behind a starved thread pool), and other callers waiting on
/// <see cref="_requestLock"/> are protected by <see cref="SemaphoreAcquireTimeout"/> regardless.</summary>
public sealed partial class OmniRigRadioProtocol : IRadioProtocol
{
    private static readonly TimeSpan SemaphoreAcquireTimeout = TimeSpan.FromSeconds(10);

    private readonly IOmniRigComClient _client;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _connectTimeout;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private volatile bool _disposed;
    private bool _connected;

    // internal, not public: IOmniRigComClient is internal (the "COM binding" seam, never exposed
    // outside this assembly, same convention as ScanlineStudio.Core.Radio.Hamlib's IHamlibNative).
    // OmniRigProtocolFactory (public, same assembly) is the only intended way to construct this.
    internal OmniRigRadioProtocol(IOmniRigComClient client, TimeSpan requestTimeout, TimeSpan connectTimeout, ILogger? logger = null)
    {
        _client = client;
        _requestTimeout = requestTimeout;
        _connectTimeout = connectTimeout;
        _logger = logger ?? NullLogger.Instance;
    }

    public string RigId => "omnirig-client";

    public RadioCapabilities Capabilities { get; private set; }

    public async Task<RadioState> PollAsync(CancellationToken ct)
    {
        await AcquireAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            return await WithTransactionTimeoutAsync(async requestCt =>
            {
                // Cheap pre-check, same role FlrigClientProtocol.PollAsync gives rig.get_xcvr --
                // every other read below short-circuits on a non-Online status instead of reading
                // stale/meaningless values from an offline rig.
                var status = await _client.GetStatusAsync(requestCt).ConfigureAwait(false);
                if (status != RigStatusX.Online)
                {
                    var statusText = await _client.GetStatusTextAsync(requestCt).ConfigureAwait(false);
                    throw new RadioProtocolException($"OmniRig reports rig status '{status}': {statusText}");
                }

                if (Capabilities == RadioCapabilities.None)
                {
                    Capabilities = RadioCapabilities.ReadFrequency | RadioCapabilities.SetFrequency |
                                   RadioCapabilities.ReadMode | RadioCapabilities.SetMode | RadioCapabilities.PttControl;

                    // Diagnostic only -- Capabilities above is the fixed set every legacy call site
                    // actually needs (no finer-grained negotiation implemented against these yet, see
                    // the implementation plan), not derived from this read.
                    var readableParams = await _client.GetReadableParamsAsync(requestCt).ConfigureAwait(false);
                    var writeableParams = await _client.GetWriteableParamsAsync(requestCt).ConfigureAwait(false);
                    Log.RigParamsObserved(_logger, readableParams, writeableParams);
                }

                var hz = await _client.GetFrequencyHzAsync(requestCt).ConfigureAwait(false);

                var txFlag = await _client.GetTxAsync(requestCt).ConfigureAwait(false);
                // PM_RX is not simply "absence of PM_TX" (legacy sets both explicitly) -- an
                // unrecognized flag (e.g. PM_UNKNOWN) is treated as "not transmitting" rather than
                // throwing, matching RadioMode's own "unrecognized wire value is not a protocol
                // error" philosophy; it is logged so a genuinely stuck poll is still visible.
                if (txFlag != RigParamX.PM_TX && txFlag != RigParamX.PM_RX)
                {
                    Log.UnrecognizedTxFlag(_logger, txFlag);
                }

                var isTransmitting = txFlag == RigParamX.PM_TX;

                var modeFlag = await _client.GetModeAsync(requestCt).ConfigureAwait(false);
                var mode = RigParamXMapper.ToRadioMode(modeFlag);

                return new RadioState(hz, mode, isTransmitting, null, DateTimeOffset.UtcNow);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public async Task SetFrequencyAsync(long hz, CancellationToken ct)
    {
        await AcquireAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            await WithTransactionTimeoutAsync(async requestCt =>
            {
                // IRigX.Freq is VT_I4 (32-bit) on the wire -- see the implementation plan's "Freq is
                // 32-bit" note. A silent truncation would key the wrong frequency, so this throws
                // rather than narrowing.
                if (hz is < 0 or > int.MaxValue)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(hz), hz, $"OmniRig's Freq property is a 32-bit signed value (max {int.MaxValue} Hz).");
                }

                await _client.SetFrequencyHzAsync((int)hz, requestCt).ConfigureAwait(false);
                return true;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public async Task SetModeAsync(RadioMode mode, CancellationToken ct)
    {
        await AcquireAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            await WithTransactionTimeoutAsync(async requestCt =>
            {
                var rigParam = RigParamXMapper.ToRigParamX(mode);
                await _client.SetModeAsync(rigParam, requestCt).ConfigureAwait(false);
                return true;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    /// <summary>Legacy sets both <c>PM_TX</c> and <c>PM_RX</c> explicitly (<c>Main.cpp</c>) -- never
    /// infers "not transmitting" from merely clearing a bit. Unlike
    /// <c>FlrigClientProtocol.SetPttAsync</c>, no readback-confirmation poll here -- OmniRig's own
    /// <c>Tx</c> property reflects a value OmniRig itself polls/caches from the rig on its own
    /// schedule (not a synchronous hardware round-trip the way flrig's <c>rig.get_ptt</c> is
    /// documented to be), so an immediate readback would likely just echo the value this method
    /// itself just set rather than confirm real hardware state -- **unverified assumption, not
    /// confirmed against any OmniRig source in this tree**, flagged per CLAUDE.md §3.</summary>
    public async Task SetPttAsync(bool tx, CancellationToken ct)
    {
        await AcquireAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            await WithTransactionTimeoutAsync(async requestCt =>
            {
                await _client.SetTxAsync(tx ? RigParamX.PM_TX : RigParamX.PM_RX, requestCt).ConfigureAwait(false);
                return true;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    /// <summary>Deliberately unsupported -- OmniRig's <c>IRigX</c> exposes no bandwidth concept at
    /// all (verified against its full DISPID 1-26 member list). Matches
    /// <c>FlrigClientProtocol.SetBandwidthAsync</c>'s identical convention: never adds
    /// <see cref="RadioCapabilities.ReadBandwidth"/>/<see cref="RadioCapabilities.SetBandwidth"/> to
    /// <see cref="Capabilities"/>, but still throws rather than silently no-op'ing for a caller that
    /// bypasses that gate.</summary>
    public Task SetBandwidthAsync(int? bandwidthHz, CancellationToken ct) =>
        throw new InvalidOperationException(
            "This backend (OmniRig) does not support setting bandwidth -- OmniRig's IRigX exposes no bandwidth property.");

    /// <summary>Connects lazily on first use, inside the same request lock every other call goes
    /// through -- avoids a separate "am I connected" race between <see cref="PollAsync"/> and a
    /// caller's own <c>Set*Async</c> reaching this backend concurrently.
    ///
    /// <b>Code-review finding (round 1, corrected round 2)</b>: given its own
    /// <see cref="_connectTimeout"/> budget, deliberately called BEFORE (not inside)
    /// <see cref="WithTransactionTimeoutAsync{T}"/> -- unlike flrig/rigctld connecting to an
    /// already-running server, this backend's own "Connection lifecycle" (spec/03-cat-layer.md) can
    /// cold-start <c>OmniRig.exe</c> itself (matching legacy's <c>ckRunningOrNew</c> default), a
    /// real process launch plus its own startup work that a plain 5s per-call budget (sized for a
    /// call to an already-running server) is too tight for. This class's own doc comment explains
    /// why neither budget can actually preempt a COM call already in flight (the real risk this
    /// separation avoids is a too-short deadline expiring while activation is still queued or
    /// legitimately starting up, not a background write into a stale RCW).</summary>
    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_connected)
        {
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_connectTimeout);
        try
        {
            await _client.ConnectAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"OmniRig did not respond to connection/activation within {_connectTimeout}.");
        }

        _connected = true;
    }

    private async Task<T> WithTransactionTimeoutAsync<T>(Func<CancellationToken, Task<T>> body, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_requestTimeout);
        try
        {
            return await body(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"OmniRig did not respond within {_requestTimeout}.");
        }
    }

    private async Task AcquireAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var acquired = await _requestLock.WaitAsync(SemaphoreAcquireTimeout, ct).ConfigureAwait(false);
        if (!acquired)
        {
            Log.SemaphoreAcquireTimedOut(_logger, SemaphoreAcquireTimeout);
            throw new TimeoutException(
                $"Could not acquire the OmniRig request lock within {SemaphoreAcquireTimeout} -- something else may be stuck holding it.");
        }

        if (_disposed)
        {
            _requestLock.Release();
            throw new ObjectDisposedException(nameof(OmniRigRadioProtocol));
        }
    }

    /// <summary>Code-review finding (round 1): must take <see cref="_requestLock"/> before releasing
    /// the COM handle -- mirrors <c>HamlibRadioProtocol.DisposeAsync</c>'s identical safety
    /// requirement. Without it, a poll or Set*Async call could still be in flight against
    /// <c>OmniRigComClient</c>'s RCW when <c>Marshal.FinalReleaseComObject</c> zeroes its ref count --
    /// documented-unsafe use of that API (at best <c>InvalidComObjectException</c>, at worst a native
    /// crash), on the one platform this repo cannot test.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _requestLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            // Deliberately NOT _requestLock.Dispose() -- see HamlibRadioProtocol.DisposeAsync's own
            // identical comment for why (a concurrent transaction's own `finally { Release(); }`
            // would throw ObjectDisposedException instead of surfacing the real in-flight exception).
            _requestLock.Release();
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Critical, Message = "Could not acquire the OmniRig request lock within {Timeout} -- something else may be stuck holding it")]
        public static partial void SemaphoreAcquireTimedOut(ILogger logger, TimeSpan timeout);

        [LoggerMessage(Level = LogLevel.Warning, Message = "OmniRig reported an unrecognized Tx flag '{TxFlag}' -- treated as not transmitting")]
        public static partial void UnrecognizedTxFlag(ILogger logger, RigParamX txFlag);

        [LoggerMessage(Level = LogLevel.Debug, Message = "OmniRig reports ReadableParams={ReadableParams}, WriteableParams={WriteableParams}")]
        public static partial void RigParamsObserved(ILogger logger, RigParamX readableParams, RigParamX writeableParams);
    }
}
