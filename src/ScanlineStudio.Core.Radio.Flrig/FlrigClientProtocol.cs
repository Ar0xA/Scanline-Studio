using System.Globalization;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Radio.Flrig;

/// <summary>See spec/03-cat-layer.md's flrig section. <see cref="IRadioProtocol"/> for flrig's XML-RPC
/// remote-control interface. Owns its <see cref="HttpClient"/> internally (constructor-injected,
/// typically built by <see cref="FlrigProtocolFactory"/> via a named <c>IHttpClientFactory</c> client
/// with <c>BaseAddress</c> already set to <c>http://Host:Port/RPC2</c>).
///
/// <b>No persistent connection to open</b> -- unlike <c>RigctldClientProtocol</c>'s TCP transport,
/// there is nothing to "connect" before the first call; every RPC is an independent HTTP request.
///
/// <b>Internal serialization</b>: a bounded-wait <see cref="SemaphoreSlim"/> (see
/// <see cref="AcquireAsync"/>) serializes every call the same way <c>RigctldClientProtocol</c>'s own
/// lock does -- not strictly required for correctness the way it is for a byte stream (flrig's own
/// server already serializes overlapping requests via its internal <c>mutex_serial</c>), but keeps
/// behavior predictable/testable and matches this project's established discipline for shared session
/// state.
///
/// <b>Timeout composition</b>: a single <see cref="_requestTimeout"/> bounds each whole public-method
/// transaction (mirrors <c>RigctldClientProtocol.WithRequestTimeoutAsync</c> wrapping its whole command
/// sequence, not each individual line) -- so a stuck poll can't hold the request lock indefinitely and
/// leave no path to unkey a live transmitter. <see cref="_verifyPollTimeout"/> is a separate, shorter
/// bound used only for the individual sub-polls inside <see cref="SetFrequencyAsync"/>'s
/// poll-until-match verification loop and <see cref="SetPttAsync"/>'s confirmation readback.</summary>
public sealed partial class FlrigClientProtocol : IRadioProtocol
{
    private static readonly TimeSpan SemaphoreAcquireTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan VerifyBudget = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan VerifyPollInterval = TimeSpan.FromMilliseconds(150);
    private const int FreqToleranceHz = 10;

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _verifyPollTimeout;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private volatile bool _disposed;

    private IReadOnlyList<string>? _cachedModes;
    private string? _lastKnownXcvrName;

    // Optional, defaulting to a no-op logger: constructed via `new` in FlrigProtocolFactory, not
    // through DI (mirrors RigctldClientProtocol's own constructor shape).
    public FlrigClientProtocol(HttpClient httpClient, TimeSpan requestTimeout, TimeSpan verifyPollTimeout, ILogger? logger = null)
    {
        _httpClient = httpClient;
        _requestTimeout = requestTimeout;
        _verifyPollTimeout = verifyPollTimeout;
        _logger = logger ?? NullLogger.Instance;
    }

    public string RigId => "flrig-client";

    public RadioCapabilities Capabilities { get; private set; }

    public async Task<RadioState> PollAsync(CancellationToken ct)
    {
        await AcquireAsync(ct).ConfigureAwait(false);
        try
        {
            return await WithTransactionTimeoutAsync(async requestCt =>
            {
                // Cheap (no serial I/O, no mutex_serial on flrig's side) and checked every poll --
                // every other read below can return a fabricated placeholder instead of a fault when
                // the transceiver isn't actually there, so this must be verified first.
                var xcvrName = await CallAsync("rig.get_xcvr", [], requestCt).ConfigureAwait(false);
                if (string.IsNullOrEmpty(xcvrName))
                {
                    throw new RadioProtocolException(
                        "flrig reports no transceiver connected (rig offline/unplugged, or flrig's own XML-RPC toggle is disabled).");
                }

                InvalidateModesCacheIfXcvrChanged(xcvrName);

                // Set once, first time a live rig is confirmed -- flrig's XML-RPC surface doesn't make
                // these optional/probeable per-session the way rigctld's `l <meter>` negotiation does,
                // they're just present-or-not based on whether a rig is attached. Sticky afterward
                // (mirrors RigctldClientProtocol's own "negotiated once, stays for the session" shape)
                // rather than flapping to None on a merely transient offline poll, which would throw
                // before any caller could observe it anyway.
                if (Capabilities == RadioCapabilities.None)
                {
                    Capabilities = RadioCapabilities.ReadFrequency | RadioCapabilities.SetFrequency |
                                   RadioCapabilities.ReadMode | RadioCapabilities.SetMode | RadioCapabilities.PttControl;
                }

                // The one live serial read per poll tick -- see FlrigClientProtocol's own class doc
                // comment and the implementation plan for why rig.get_vfoA (not the cached rig.get_vfo).
                var freqText = await CallAsync("rig.get_vfoA", [], requestCt).ConfigureAwait(false);
                if (freqText is null ||
                    !long.TryParse(freqText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hz))
                {
                    throw new RadioProtocolException($"flrig 'rig.get_vfoA' returned an unparseable frequency: '{freqText}'.");
                }

                var modeText = await CallAsync("rig.get_mode", [], requestCt).ConfigureAwait(false);
                var mode = modeText is not null && FlrigModeTokens.TokenToMode.TryGetValue(modeText, out var mappedMode)
                    ? mappedMode
                    : RadioMode.Unknown;

                var pttText = await CallAsync("rig.get_ptt", [], requestCt).ConfigureAwait(false);
                var isTransmitting = pttText is not null && pttText != "0";

                // No SignalStrengthDb/SwrRatio/AlcLevel/PowerPercent -- meter RPCs are out of scope
                // for this backend (see the implementation plan's "explicitly out of scope" note).
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
            await WithTransactionTimeoutAsync(async requestCt =>
            {
                var setResult = await CallAsync("rig.set_vfoA", [(double)hz], requestCt).ConfigureAwait(false);
                if (setResult == "0")
                {
                    throw new RadioProtocolException(
                        "flrig refused 'rig.set_vfoA' -- rig offline or flrig's own XML-RPC toggle is disabled.");
                }

                // rig.set_vfoA's success path never sets a response value (confirmed against source) --
                // no positive-success signal exists on the wire, so verify via a bounded
                // poll-until-match readback against the live rig.get_vfoA read instead. Real worst
                // case is VerifyBudget plus one extra VerifyPollInterval+VerifyPollTimeout (~2.65s at
                // this class's own defaults), not a hard cap at VerifyBudget alone -- the deadline is
                // checked after each sleep+poll, so the loop always completes at least one full poll.
                // Still comfortably bounded by the 5s outer transaction timeout either way.
                var deadline = DateTime.UtcNow + VerifyBudget;
                while (true)
                {
                    await Task.Delay(VerifyPollInterval, requestCt).ConfigureAwait(false);
                    var actualText = await CallWithOwnTimeoutAsync("rig.get_vfoA", _verifyPollTimeout, requestCt)
                        .ConfigureAwait(false);
                    if (actualText is not null &&
                        long.TryParse(actualText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var actual) &&
                        Math.Abs(actual - hz) <= FreqToleranceHz)
                    {
                        return true;
                    }

                    if (DateTime.UtcNow >= deadline)
                    {
                        throw new RadioProtocolException(
                            $"flrig did not confirm 'rig.set_vfoA({hz})' within {VerifyBudget}.");
                    }
                }
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
            await WithTransactionTimeoutAsync(async requestCt =>
            {
                if (!FlrigModeTokens.Candidates.TryGetValue(mode, out var candidates))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(mode), mode, "This RadioMode has no flrig wire-format equivalent.");
                }

                var modesList = await GetOrRefreshModesCacheAsync(requestCt).ConfigureAwait(false);
                var token = candidates.FirstOrDefault(candidate => modesList.Contains(candidate, StringComparer.Ordinal));
                if (token is null)
                {
                    // No RPC call at all -- flrig would silently no-op on an unmatched token anyway,
                    // so failing here is both faster and more precise than sending it and waiting.
                    throw new RadioProtocolException(
                        $"The connected rig has no '{mode}' equivalent in flrig's reported mode list.");
                }

                var result = await CallAsync("rig.set_mode", [token], requestCt).ConfigureAwait(false);
                if (result == "1")
                {
                    // Applied -- a reliable, discrete signal (confirmed against source: set immediately
                    // after the mode is actually applied), no readback needed.
                    return true;
                }

                if (result == "0")
                {
                    throw new RadioProtocolException(
                        "flrig refused 'rig.set_mode' -- rig offline or flrig's own XML-RPC toggle is disabled.");
                }

                // Unset/empty -- flrig's own case-sensitive match against the rig's real mode list
                // failed. Shouldn't happen given the pre-check above; defensive against the cached
                // list going stale (e.g. the rig was swapped mid-session and get_xcvr's name happened
                // not to change either).
                throw new RadioProtocolException(
                    $"flrig did not apply mode token '{token}' for '{mode}' (no match against the rig's own mode list).");
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    /// <summary>flrig's own <c>rig.set_ptt</c> handler already retry-polls the actual keyed state for
    /// up to ~1s server-side before returning, and writes its result from a genuinely observed
    /// <c>ptt_state()</c> read (confirmed against source) -- so the one bounded client-side readback
    /// below is a real signal, not a pure echo, on most rig drivers. Known, documented exception: on
    /// the FT-817/FT-817BB/FT-818ND drivers specifically, flrig's background PTT-state refresh
    /// early-returns and never re-reads from hardware, so <c>rig.get_ptt</c> only echoes the last
    /// value this app itself requested there -- a failed physical unkey on those specific rigs is
    /// undetectable via this readback alone. The app-level un-key retry in
    /// <c>RadioSessionService.TryUnkeyWithRetryAsync</c> is the real safety net regardless of this
    /// backend's own readback outcome.</summary>
    public async Task SetPttAsync(bool tx, CancellationToken ct)
    {
        await AcquireAsync(ct).ConfigureAwait(false);
        try
        {
            await WithTransactionTimeoutAsync(async requestCt =>
            {
                var result = await CallAsync("rig.set_ptt", [tx ? 1 : 0], requestCt).ConfigureAwait(false);
                if (result == "0")
                {
                    throw new RadioProtocolException(
                        $"flrig refused 'rig.set_ptt({(tx ? 1 : 0)})' -- rig offline or flrig's own XML-RPC toggle is disabled.");
                }

                var confirmedText = await CallWithOwnTimeoutAsync("rig.get_ptt", _verifyPollTimeout, requestCt)
                    .ConfigureAwait(false);
                // Code-review finding: a null confirmedText (the readback itself timed out) must be
                // "unconfirmed" regardless of the requested direction -- rig.get_ptt always assigns a
                // response server-side (xml_server.cxx:280-290), so null here means only a transport
                // stall, never a genuine "PTT is off" reading. The original `confirmed != tx` check
                // treated null (confirmed=false) as a MATCH when tx was also false, which could report
                // a timed-out un-key readback as a confirmed un-key -- exactly the case
                // TryUnkeyWithRetryAsync depends on this method to catch, not paper over.
                if (confirmedText is null)
                {
                    throw new RadioProtocolException($"flrig did not respond to 'rig.get_ptt' while confirming PTT {(tx ? "on" : "off")}.");
                }

                var confirmed = confirmedText != "0";
                if (confirmed != tx)
                {
                    throw new RadioProtocolException($"flrig did not confirm PTT {(tx ? "on" : "off")} after 'rig.set_ptt'.");
                }

                return true;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    /// <summary>Deliberately unsupported, not implemented against flrig's real
    /// <c>rig.set_bandwidth</c>/<c>rig.get_bw</c> RPCs. <c>rig.set_bandwidth</c> itself does take Hz
    /// (server-side, per flrig's <c>xml_server.cxx</c>: label-table rigs snap to the nearest
    /// available value, int-bandwidth rigs clamp/round it), so the SET half is not the blocker. The
    /// READ half is: <c>rig.get_bw</c> returns strings straight from the rig's own bandwidth table --
    /// sometimes a numeric label, sometimes a non-numeric one ("WIDE"/"NORMAL"/"NARROW" on several
    /// Icom rigs), sometimes a dual lo/hi DSP pair -- with no general, reliable mapping into
    /// <see cref="RadioState.BandwidthHz"/> without per-rig table data this port doesn't have. A
    /// set-only capability with no trustworthy readback would let the UI show a value it can never
    /// confirm was actually applied, which is worse than not offering it -- see
    /// spec/14-roadmap.md's backlog entry for the deferred full implementation. Never adds
    /// <see cref="RadioCapabilities.ReadBandwidth"/>/<see cref="RadioCapabilities.SetBandwidth"/> to
    /// <see cref="Capabilities"/>, so capability-gated UI never reaches this call in practice; it still
    /// throws rather than silently no-op'ing for any caller that bypasses that gate.</summary>
    public Task SetBandwidthAsync(int? bandwidthHz, CancellationToken ct) =>
        throw new InvalidOperationException(
            "This backend (flrig) does not support setting bandwidth -- flrig's own bandwidth readback " +
            "isn't reliably convertible to Hz across rigs, so this port doesn't offer a set path either.");

    private void InvalidateModesCacheIfXcvrChanged(string xcvrName)
    {
        if (_lastKnownXcvrName is not null && !string.Equals(_lastKnownXcvrName, xcvrName, StringComparison.Ordinal))
        {
            _cachedModes = null;
            Log.RigSwapDetected(_logger, _lastKnownXcvrName, xcvrName);
        }

        _lastKnownXcvrName = xcvrName;
    }

    /// <summary>Only caches a non-empty <c>rig.get_modes</c> result -- an empty/malformed response
    /// (flrig's own confirmed bug on its offline/exception paths, see <see cref="XmlRpcCodec"/>) must
    /// never be mistaken for "this rig supports no modes." Retries on every call while the cache is
    /// still empty, not just once at connect.</summary>
    private async Task<IReadOnlyList<string>> GetOrRefreshModesCacheAsync(CancellationToken ct)
    {
        if (_cachedModes is { Count: > 0 })
        {
            return _cachedModes;
        }

        var modes = await CallArrayAsync("rig.get_modes", [], ct).ConfigureAwait(false);
        if (modes.Count > 0)
        {
            _cachedModes = modes;
        }

        return modes;
    }

    // object[] args (not `params`) is deliberate -- CA1068 requires CancellationToken to be the last
    // parameter, and `params` must also be the last parameter, so the two can't coexist here. Call
    // sites pass an explicit array literal instead.
    private async Task<string?> CallAsync(string method, object[] args, CancellationToken ct)
    {
        var body = await SendAsync(method, args, ct).ConfigureAwait(false);
        return XmlRpcCodec.ParseScalarResponse(body);
    }

    private async Task<IReadOnlyList<string>> CallArrayAsync(string method, object[] args, CancellationToken ct)
    {
        var body = await SendAsync(method, args, ct).ConfigureAwait(false);
        return XmlRpcCodec.ParseArrayResponse(body);
    }

    private async Task<string> SendAsync(string method, object[] args, CancellationToken ct)
    {
        var requestBytes = XmlRpcCodec.BuildRequest(method, args);
        using var content = new ByteArrayContent(requestBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/xml");
        // A non-2xx status or a connection failure throws HttpRequestException here, uncaught --
        // transport-level, matching RigctldClientProtocol's treatment of a transport break.
        using var response = await _httpClient.PostAsync(string.Empty, content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Like <see cref="CallAsync"/>, but bounded by <paramref name="timeout"/> instead of the
    /// enclosing transaction's own <see cref="_requestTimeout"/>, and treats its own timeout as "no
    /// confirmation yet" (returns <see langword="null"/>) rather than a hard failure -- used only by
    /// the poll-until-match verification loops, where one slow sub-poll should not itself end the
    /// operation; the loop's own wall-clock budget governs that instead. Mirrors the sibling sdrsync
    /// client's own <c>_poll_call</c> pattern for the identical problem.</summary>
    private async Task<string?> CallWithOwnTimeoutAsync(string method, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await CallAsync(method, [], cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Bounds one whole public-method transaction against <paramref name="ct"/> -- mirrors
    /// <c>RigctldClientProtocol.WithRequestTimeoutAsync</c> wrapping its whole command sequence (not
    /// each individual RPC), for the same reason: an unbounded multi-call method could hold the
    /// request lock far longer than intended, and if this protocol is ever reached while a real
    /// transmit session is keyed, a stuck transaction would leave no path to unkey it. A timeout
    /// surfaces as <see cref="TimeoutException"/> (transport-level, so <c>RadioController</c> backs
    /// off and rebuilds rather than retrying command-level).</summary>
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
            throw new TimeoutException($"flrig did not respond within {_requestTimeout}.");
        }
    }

    /// <summary>Acquires <see cref="_requestLock"/> with a bounded wait -- even when the caller passes
    /// <see cref="CancellationToken.None"/> (as <c>RadioSessionService.TryUnkeyWithRetryAsync</c>'s
    /// own un-key retry deliberately does), the wait still respects <see cref="SemaphoreAcquireTimeout"/>
    /// and fails loudly instead of blocking forever if something else is stuck holding the lock. The
    /// wait happens BEFORE the per-transaction timeout in <see cref="WithTransactionTimeoutAsync"/>
    /// starts counting, so time spent waiting for the lock never eats into a request's own timeout
    /// budget.</summary>
    private async Task AcquireAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var acquired = await _requestLock.WaitAsync(SemaphoreAcquireTimeout, ct).ConfigureAwait(false);
        if (!acquired)
        {
            Log.SemaphoreAcquireTimedOut(_logger, SemaphoreAcquireTimeout);
            throw new TimeoutException(
                $"Could not acquire the flrig request lock within {SemaphoreAcquireTimeout} -- something else may be stuck holding it.");
        }

        if (_disposed)
        {
            _requestLock.Release();
            throw new ObjectDisposedException(nameof(FlrigClientProtocol));
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            // Safe to dispose an IHttpClientFactory-created HttpClient directly -- the underlying
            // handler is pooled/managed internally regardless (documented .NET behavior).
            _httpClient.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Critical, Message = "Could not acquire the flrig request lock within {Timeout} -- something else may be stuck holding it")]
        public static partial void SemaphoreAcquireTimedOut(ILogger logger, TimeSpan timeout);

        [LoggerMessage(Level = LogLevel.Information, Message = "flrig reports a different transceiver ('{OldName}' -> '{NewName}') -- mode list cache invalidated")]
        public static partial void RigSwapDetected(ILogger logger, string oldName, string newName);
    }
}
