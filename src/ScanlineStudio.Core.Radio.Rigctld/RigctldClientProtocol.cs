using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Radio.Rigctld;

/// <summary>
/// See spec/04-rigctld.md. <see cref="IRadioProtocol"/> for `rigctld`'s backward-compatible
/// single-letter line protocol. Owns its <see cref="IRadioTransport"/> internally (typically a
/// <see cref="TcpTransport"/>, constructor-injected by <see cref="RigctldProtocolFactory"/> --
/// injected as the interface, not the concrete type, so tests can substitute
/// <see cref="FakeRadioTransport"/>).
///
/// Lazily opens the transport and negotiates capabilities (probing `f`/`m`/`t` once -- see
/// spec/04-rigctld.md's "Discovery and capability negotiation") on the first call to any public
/// method, not in the constructor (which can't be async).
///
/// <b>Internal serialization</b>: <see cref="IRadioController"/> can call <see cref="PollAsync"/> from
/// its own background poll-loop thread while a caller invokes a `Set*Async` method from a different
/// thread at the same time -- both would otherwise interleave requests/responses on the same
/// <see cref="IRadioTransport"/>, which is explicitly single-consumer-only and undefined under
/// concurrent use. A <see cref="SemaphoreSlim"/> serializes every request/response transaction
/// (including the initial connect+probe) so only one is ever in flight against the transport.
/// </summary>
public sealed partial class RigctldClientProtocol : IRadioProtocol
{
    // Hamlib's own mode-token vocabulary (verified against a local Hamlib source clone,
    // src/misc.c's mode_str[] table -- see spec/04-rigctld.md). RadioMode's Data/DataR/Pkt split has
    // no legacy precedent to verify against (spec/04: "new capability, no direct legacy equivalent") --
    // this mapping is a documented design choice, not a verified fact: Data/DataR follow the same
    // "normal vs. reversed sideband" convention as Cw/CwR and Rtty/RttyR (PKTUSB = data-normal,
    // PKTLSB = data-reversed), and Pkt (no normal/reversed pairing, since FM has no sideband to
    // reverse) maps to Hamlib's PKTFM.
    private static readonly Dictionary<string, RadioMode> TokenToMode =
        new Dictionary<string, RadioMode>(StringComparer.Ordinal)
        {
            ["USB"] = RadioMode.Usb,
            ["LSB"] = RadioMode.Lsb,
            ["CW"] = RadioMode.Cw,
            ["CWR"] = RadioMode.CwR,
            ["CW-R"] = RadioMode.CwR,
            ["AM"] = RadioMode.Am,
            ["FM"] = RadioMode.Fm,
            ["RTTY"] = RadioMode.Rtty,
            ["RTTYR"] = RadioMode.RttyR,
            ["RTTY-R"] = RadioMode.RttyR,
            ["PKTUSB"] = RadioMode.Data,
            ["USB-D"] = RadioMode.Data,
            ["PKTLSB"] = RadioMode.DataR,
            ["LSB-D"] = RadioMode.DataR,
            ["PKTFM"] = RadioMode.Pkt,
        };

    private static readonly Dictionary<RadioMode, string> ModeToToken =
        new Dictionary<RadioMode, string>
        {
            [RadioMode.Usb] = "USB",
            [RadioMode.Lsb] = "LSB",
            [RadioMode.Cw] = "CW",
            [RadioMode.CwR] = "CWR",
            [RadioMode.Am] = "AM",
            [RadioMode.Fm] = "FM",
            [RadioMode.Rtty] = "RTTY",
            [RadioMode.RttyR] = "RTTYR",
            [RadioMode.Data] = "PKTUSB",
            [RadioMode.DataR] = "PKTLSB",
            [RadioMode.Pkt] = "PKTFM",
        };

    // Bounds one whole request/response transaction (and, separately, the capability probe -- see
    // EnsureConnectedAsync). Without it, a peer that accepts the socket and then stops answering (a
    // half-open TCP after a remote crash, a stopped rigctld) blocked the reading caller forever WHILE
    // HOLDING _requestLock -- every later caller, including a PTT unkey, queued behind it and never
    // reached the wire, leaving a physically keyed transmitter with no path to unkey it.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly IRadioTransport _transport;
    private readonly TimeSpan _connectTimeout;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    // volatile: unlike HamlibRadioProtocol's _disposed (ordered by their shared semaphore, since
    // DisposeAsync there takes the same lock), DisposeAsync here never takes _requestLock at all, so
    // there is no other happens-before edge between its write and AcquireAsync's own post-wait read.
    private volatile bool _disposed;

    // Meter reads happen up to 3x per poll while transmitting -- logging every soft-failure
    // unconditionally would be a hot-path violation for a long transmission on a rig that
    // intermittently errors one meter (see docs/logging-guidelines.md's poll-loop rule). Tracked
    // per command (not a single flag) since SWR/ALC/power can independently flap; PollAsync only
    // ever calls TryGetMeterAsync sequentially under _requestLock, so a plain Dictionary is safe.
    private readonly Dictionary<string, bool> _meterLastReadFailed = new();

    // Optional, defaulting to a no-op logger: constructed via `new` in RigctldProtocolFactory,
    // not through DI. RigctldProtocolFactory does pass its own real ILogger through today.
    public RigctldClientProtocol(IRadioTransport transport, TimeSpan connectTimeout, ILogger? logger = null)
    {
        _transport = transport;
        _connectTimeout = connectTimeout;
        _logger = logger ?? NullLogger.Instance;
    }

    public string RigId => "rigctld-client";

    public RadioCapabilities Capabilities { get; private set; }

    public async Task<RadioState> PollAsync(CancellationToken ct)
    {
        await AcquireAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);

            return await WithRequestTimeoutAsync(async requestCt =>
            {
                // Frequency is core to RadioState -- always attempted regardless of the probed
                // capability, unlike mode/PTT below (which have meaningful defaults when absent).
                var freqLine = await GetSingleLineOrThrowAsync("f", requestCt).ConfigureAwait(false);
                if (!long.TryParse(freqLine, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hz))
                {
                    throw new RadioProtocolException($"rigctld 'f' returned an unparseable frequency: '{freqLine}'.");
                }

                var mode = RadioMode.Unknown;
                if (Capabilities.HasFlag(RadioCapabilities.ReadMode))
                {
                    mode = await GetModeAsync(requestCt).ConfigureAwait(false);
                }

                var isTransmitting = false;
                if (Capabilities.HasFlag(RadioCapabilities.PttControl))
                {
                    var pttLine = await GetSingleLineOrThrowAsync("t", requestCt).ConfigureAwait(false);
                    if (!int.TryParse(pttLine, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pttValue))
                    {
                        // Throws rather than defaulting to false, matching the frequency read above:
                        // "not transmitting" is the one wrong guess with physical consequences here, and
                        // it also suppresses this poll's SWR/ALC/power reads (gated on isTransmitting
                        // below), so a garbled readback would silently disarm the SWR cutoff on a rig
                        // that IS keyed. RadioProtocolException is command-level -- RadioController
                        // keeps cadence and publishes CommandFailed instead of tearing the connection
                        // down.
                        throw new RadioProtocolException(
                            $"rigctld 't' returned an unparseable PTT state: '{pttLine}'.");
                    }

                    isTransmitting = pttValue != 0;
                }

                // Meters are TX-only readings on a real rig -- gated on the PTT readback already
                // obtained above (not a separate always-on probe) both because an RX-time read is
                // meaningless and because it would double this poll's round-trip count on every cycle
                // for a reading nobody looks at outside an active transmit.
                float? swr = null, alc = null, powerPercent = null;
                if (isTransmitting)
                {
                    if (Capabilities.HasFlag(RadioCapabilities.SwrMeter))
                    {
                        swr = await TryGetMeterAsync("l SWR", requestCt).ConfigureAwait(false);
                    }

                    if (Capabilities.HasFlag(RadioCapabilities.AlcMeter))
                    {
                        alc = await TryGetMeterAsync("l ALC", requestCt).ConfigureAwait(false);
                    }

                    if (Capabilities.HasFlag(RadioCapabilities.PowerMeter))
                    {
                        var fraction = await TryGetMeterAsync("l RFPOWER_METER", requestCt).ConfigureAwait(false);
                        powerPercent = fraction * 100f;
                    }
                }

                // Opposite gating from the TX-only meters above -- see RadioState.SignalStrengthDb's
                // own doc comment. RIG_LEVEL_STRENGTH is documented "arg int (dB)" in rig.h (unlike
                // SWR/ALC/RFPOWER_METER, which are float) -- rigctld's `l <LEVEL>` line protocol still
                // emits it as a single plain-text line either way (verified against a local Hamlib
                // clone, rigctl_parse.c), so the existing float-parsing TryGetMeterAsync/ParseMeterFloat
                // still parses it correctly; only the STORED type differs (rounded to int here to
                // match RadioState.SignalStrengthDb's own int? type).
                int? signalStrengthDb = null;
                if (!isTransmitting && Capabilities.HasFlag(RadioCapabilities.SignalMeter))
                {
                    var raw = await TryGetMeterAsync("l STRENGTH", requestCt).ConfigureAwait(false);
                    signalStrengthDb = raw is { } v && !float.IsNaN(v) && !float.IsInfinity(v)
                        ? (int)MathF.Round(v)
                        : null;
                }

                return new RadioState(hz, mode, isTransmitting, signalStrengthDb, DateTimeOffset.UtcNow, swr, alc, powerPercent);
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
            await WithRequestTimeoutAsync(requestCt => SendSetCommandAsync($"F {hz}", requestCt), ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public async Task SetModeAsync(RadioMode mode, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ModeToToken.TryGetValue(mode, out var token))
        {
            throw new ArgumentOutOfRangeException(
                nameof(mode), mode, "This RadioMode has no rigctld wire-format equivalent.");
        }

        await AcquireAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            // Passband 0 = Hamlib's RIG_PASSBAND_NORMAL sentinel ("use the rig's default passband
            // for this mode") -- verified against hamlib/include/hamlib/rig.h. RadioState has no
            // passband field, so this is always what ScanlineStudio asks for.
            await WithRequestTimeoutAsync(requestCt => SendSetCommandAsync($"M {token} 0", requestCt), ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public async Task SetPttAsync(bool tx, CancellationToken ct)
    {
        await AcquireAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            await WithRequestTimeoutAsync(requestCt => SendSetCommandAsync($"T {(tx ? 1 : 0)}", requestCt), ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_transport.IsOpen)
        {
            return;
        }

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(_connectTimeout);
        try
        {
            await _transport.OpenAsync(connectCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.ConnectTimedOut(_logger, _connectTimeout);
            throw new TimeoutException($"Connecting to rigctld timed out after {_connectTimeout}.");
        }

        // Bounded separately from the OpenAsync step above (which has its own configurable
        // _connectTimeout): 7 round trips with no bound of their own would otherwise wedge just like
        // any other unbounded request/response transaction -- see RequestTimeout's own doc comment.
        Capabilities = await WithRequestTimeoutAsync(ProbeCapabilitiesAsync, ct).ConfigureAwait(false);
        Log.CapabilitiesNegotiated(_logger, Capabilities);
    }

    /// <summary>Bounds one whole request/response transaction against <paramref name="ct"/> -- see
    /// <see cref="RequestTimeout"/>'s own doc comment for why. A timeout surfaces as
    /// <see cref="TimeoutException"/> (transport-level, so <c>RadioController</c> backs off and rebuilds
    /// the protocol from scratch rather than retrying command-level); the cancelled read that produces
    /// it also aborts the underlying socket per <see cref="IRadioTransport"/>'s own contract, so the
    /// next connect starts clean instead of reading a desynced stream.</summary>
    private static async Task<T> WithRequestTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> body, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);
        try
        {
            return await body(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"rigctld did not respond within {RequestTimeout}.");
        }
    }

    private static async Task WithRequestTimeoutAsync(Func<CancellationToken, Task> body, CancellationToken ct)
    {
        await WithRequestTimeoutAsync(async innerCt =>
        {
            await body(innerCt).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Probes `f`/`m`/`t` once and sets capability flags from which return a value vs. an
    /// `RPRT` error -- see spec/04-rigctld.md's "Discovery and capability negotiation" for why this
    /// replaces `\dump_caps` parsing. `Set*` capability is assumed to mirror the corresponding `Get*`
    /// capability (rigctld's backward-compatible protocol has no separate "can-set" probe, and
    /// actually attempting a set just to test it would have a real side effect on the rig) -- the only
    /// practical option without probing via a mutating command.</summary>
    private async Task<RadioCapabilities> ProbeCapabilitiesAsync(CancellationToken ct)
    {
        var caps = RadioCapabilities.None;

        await WriteCommandAsync("f", ct).ConfigureAwait(false);
        var freqLine = await ReadLineAsync(ct).ConfigureAwait(false);
        if (!IsErrorLine(freqLine))
        {
            caps |= RadioCapabilities.ReadFrequency | RadioCapabilities.SetFrequency;
        }

        await WriteCommandAsync("m", ct).ConfigureAwait(false);
        var modeLine = await ReadLineAsync(ct).ConfigureAwait(false);
        if (!IsErrorLine(modeLine))
        {
            _ = await ReadLineAsync(ct).ConfigureAwait(false); // passband line -- consumed to stay in sync
            caps |= RadioCapabilities.ReadMode | RadioCapabilities.SetMode;
        }

        await WriteCommandAsync("t", ct).ConfigureAwait(false);
        var pttLine = await ReadLineAsync(ct).ConfigureAwait(false);
        if (!IsErrorLine(pttLine))
        {
            caps |= RadioCapabilities.PttControl;
        }

        // Extended-level `l <LEVEL>` probes (spec/04-rigctld.md's telemetry section -- supersedes
        // that doc's earlier "extended-level commands are out of v1 scope" Non-goals entry, updated
        // alongside this). Verified directly against a local Hamlib clone's rigctl_parse.c
        // (declare_proto_rig(get_level)): rigctld runs with interactive=1/prompt=0, so a supported
        // float level (SWR/ALC/RFPOWER_METER all are, per RIG_LEVEL_FLOAT_LIST in rig.h) responds
        // with exactly one `%g` line -- same shape as f/m/t above, same RPRT-error-means-absent
        // capability-negotiation convention.
        await WriteCommandAsync("l SWR", ct).ConfigureAwait(false);
        if (!IsErrorLine(await ReadLineAsync(ct).ConfigureAwait(false)))
        {
            caps |= RadioCapabilities.SwrMeter;
        }

        await WriteCommandAsync("l ALC", ct).ConfigureAwait(false);
        if (!IsErrorLine(await ReadLineAsync(ct).ConfigureAwait(false)))
        {
            caps |= RadioCapabilities.AlcMeter;
        }

        await WriteCommandAsync("l RFPOWER_METER", ct).ConfigureAwait(false);
        if (!IsErrorLine(await ReadLineAsync(ct).ConfigureAwait(false)))
        {
            caps |= RadioCapabilities.PowerMeter;
        }

        // RIG_LEVEL_STRENGTH is an int-typed level (rig.h), unlike the three float-typed levels
        // above -- still probed the same way: rigctld's `l <LEVEL>` responds with exactly one line
        // either way, RPRT-error-means-absent.
        await WriteCommandAsync("l STRENGTH", ct).ConfigureAwait(false);
        if (!IsErrorLine(await ReadLineAsync(ct).ConfigureAwait(false)))
        {
            caps |= RadioCapabilities.SignalMeter;
        }

        return caps;
    }

    /// <summary>Reads one `l &lt;LEVEL&gt;` meter value -- unlike <see cref="GetSingleLineOrThrowAsync"/>,
    /// a failure (RPRT error, or an unparseable line) returns <see langword="null"/> instead of
    /// throwing: meters are far more likely than f/m/t to intermittently error (e.g. a rig that
    /// reports ENAVAIL for SWR while not actually keyed, a transient read glitch), and letting that
    /// abort the entire <see cref="PollAsync"/> snapshot would freeze the whole frequency/mode strip
    /// for as long as the condition lasts -- a per-meter <see langword="null"/> is the correct
    /// "unknown this poll" outcome, not a poll-level failure.</summary>
    private async Task<float?> TryGetMeterAsync(string command, CancellationToken ct)
    {
        await WriteCommandAsync(command, ct).ConfigureAwait(false);
        var line = await ReadLineAsync(ct).ConfigureAwait(false);
        if (IsErrorLine(line))
        {
            // Gated by state transition, not every failed read -- see this class's own
            // _meterLastReadFailed field doc comment for why.
            if (!_meterLastReadFailed.GetValueOrDefault(command))
            {
                _meterLastReadFailed[command] = true;
                Log.MeterReadFailed(_logger, command, line);
            }

            return null;
        }

        if (_meterLastReadFailed.Remove(command))
        {
            Log.MeterReadRecovered(_logger, command);
        }

        var value = ParseMeterFloat(line);
        if (value is null)
        {
            Log.MeterReadUnparseable(_logger, command, line);
        }

        return value;
    }

    /// <summary>Culture-invariant on purpose (a real bug caught before shipping: the default
    /// <see cref="float.TryParse(string, out float)"/> overload uses the current culture, where e.g.
    /// nl-NL/de-DE treat '.' as a *thousands* separator -- "1.5" would parse as 15, turning a normal
    /// SWR reading into an instant false cutoff trip). SWR's documented range is "0.0 ... infinite"
    /// (rig.h) -- Hamlib's own `%g` printf can legitimately emit "inf"; treated as
    /// <see cref="float.PositiveInfinity"/> (a real, cutoff-worthy value) rather than a parse failure,
    /// with "-inf"/"nan" handled the same way ("nan" maps to <see langword="null"/> -- not a known-bad
    /// direction, so it must not silently arm a cutoff).</summary>
    private static float? ParseMeterFloat(string line)
    {
        if (float.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return line.Trim().ToLowerInvariant() switch
        {
            "inf" or "+inf" or "infinity" => float.PositiveInfinity,
            "-inf" or "-infinity" => float.NegativeInfinity,
            _ => null,
        };
    }

    private async Task<RadioMode> GetModeAsync(CancellationToken ct)
    {
        await WriteCommandAsync("m", ct).ConfigureAwait(false);
        var modeLine = await ReadLineAsync(ct).ConfigureAwait(false);
        ThrowIfErrorLine(modeLine);
        _ = await ReadLineAsync(ct).ConfigureAwait(false); // passband -- not modeled in RadioState
        return TokenToMode.GetValueOrDefault(modeLine, RadioMode.Unknown);
    }

    private async Task<string> GetSingleLineOrThrowAsync(string command, CancellationToken ct)
    {
        await WriteCommandAsync(command, ct).ConfigureAwait(false);
        var line = await ReadLineAsync(ct).ConfigureAwait(false);
        ThrowIfErrorLine(line);
        return line;
    }

    private async Task SendSetCommandAsync(string command, CancellationToken ct)
    {
        await WriteCommandAsync(command, ct).ConfigureAwait(false);
        var line = await ReadLineAsync(ct).ConfigureAwait(false);
        if (!IsErrorLine(line))
        {
            throw new RadioProtocolException(
                $"Unexpected rigctld response to '{command}': '{line}' (expected an RPRT line).");
        }

        var codeText = line["RPRT ".Length..];
        if (!int.TryParse(codeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) || code != 0)
        {
            throw new RadioProtocolException($"rigctld command '{command}' failed: {line}");
        }
    }

    private async Task WriteCommandAsync(string command, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(command + "\n");
        await _transport.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var bytes = new List<byte>();
        await foreach (var b in _transport.ReadAsync(ct).ConfigureAwait(false))
        {
            if (b == (byte)'\n')
            {
                break;
            }

            bytes.Add(b);
        }

        return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
    }

    private static bool IsErrorLine(string line) => line.StartsWith("RPRT ", StringComparison.Ordinal);

    private static void ThrowIfErrorLine(string line)
    {
        if (IsErrorLine(line))
        {
            throw new RadioProtocolException($"rigctld command failed: {line}");
        }
    }

    /// <summary>Acquires <see cref="_requestLock"/> and re-checks <see cref="_disposed"/> AFTER the
    /// wait, not only before it -- same reasoning as <c>HamlibRadioProtocol.AcquireAsync</c>'s own doc
    /// comment. <see cref="DisposeAsync"/> doesn't take this lock at all (unlike Hamlib's), so the
    /// specific hazard here is narrower -- a caller already queued on <c>WaitAsync</c> when
    /// <see cref="DisposeAsync"/> runs would otherwise proceed into <see cref="EnsureConnectedAsync"/>
    /// against an already-disposed <see cref="_transport"/>, today caught only incidentally by
    /// <c>TcpTransport.OpenAsync</c>'s own <see cref="ObjectDisposedException"/> guard -- correctness
    /// by accident, not by this class's own design.</summary>
    private async Task AcquireAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _requestLock.WaitAsync(ct).ConfigureAwait(false);
        if (_disposed)
        {
            _requestLock.Release();
            throw new ObjectDisposedException(nameof(RigctldClientProtocol));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _transport.DisposeAsync().ConfigureAwait(false);
        // Deliberately NOT _requestLock.Dispose(): SemaphoreSlim only needs disposal if
        // AvailableWaitHandle was ever touched (it never is here). Disposing it made an in-flight
        // transaction's own `finally { _requestLock.Release(); }` throw ObjectDisposedException --
        // masking the real IOException from the socket the line above just aborted -- and left any
        // caller already queued on WaitAsync waiting forever, since Release() throws before it
        // increments the count and Dispose() does not fault pending waiters. A
        // SetPttAsync(false, CancellationToken.None) landing in that window would never reach the wire.
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "rigctld connect timed out after {ConnectTimeout}")]
        public static partial void ConnectTimedOut(ILogger logger, TimeSpan connectTimeout);

        [LoggerMessage(Level = LogLevel.Information, Message = "rigctld capabilities negotiated: {Capabilities}")]
        public static partial void CapabilitiesNegotiated(ILogger logger, RadioCapabilities capabilities);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Meter read '{Command}' returned an error line: '{Line}'")]
        public static partial void MeterReadFailed(ILogger logger, string command, string line);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Meter read '{Command}' recovered after a prior error")]
        public static partial void MeterReadRecovered(ILogger logger, string command);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Meter read '{Command}' returned an unparseable value: '{Line}'")]
        public static partial void MeterReadUnparseable(ILogger logger, string command, string line);
    }
}
