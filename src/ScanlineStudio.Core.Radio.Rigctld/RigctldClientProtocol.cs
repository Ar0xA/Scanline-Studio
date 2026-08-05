using System.Text;
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
public sealed class RigctldClientProtocol : IRadioProtocol
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

    private readonly IRadioTransport _transport;
    private readonly TimeSpan _connectTimeout;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private bool _disposed;

    public RigctldClientProtocol(IRadioTransport transport, TimeSpan connectTimeout)
    {
        _transport = transport;
        _connectTimeout = connectTimeout;
    }

    public string RigId => "rigctld-client";

    public RadioCapabilities Capabilities { get; private set; }

    public async Task<RadioState> PollAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _requestLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);

            // Frequency is core to RadioState -- always attempted regardless of the probed
            // capability, unlike mode/PTT below (which have meaningful defaults when absent).
            var freqLine = await GetSingleLineOrThrowAsync("f", ct).ConfigureAwait(false);
            if (!long.TryParse(freqLine, out var hz))
            {
                throw new RadioProtocolException($"rigctld 'f' returned an unparseable frequency: '{freqLine}'.");
            }

            var mode = RadioMode.Unknown;
            if (Capabilities.HasFlag(RadioCapabilities.ReadMode))
            {
                mode = await GetModeAsync(ct).ConfigureAwait(false);
            }

            var isTransmitting = false;
            if (Capabilities.HasFlag(RadioCapabilities.PttControl))
            {
                var pttLine = await GetSingleLineOrThrowAsync("t", ct).ConfigureAwait(false);
                isTransmitting = int.TryParse(pttLine, out var pttValue) && pttValue != 0;
            }

            return new RadioState(hz, mode, isTransmitting, SignalStrengthDb: null, DateTimeOffset.UtcNow);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public async Task SetFrequencyAsync(long hz, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _requestLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            await SendSetCommandAsync($"F {hz}", ct).ConfigureAwait(false);
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

        await _requestLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            // Passband 0 = Hamlib's RIG_PASSBAND_NORMAL sentinel ("use the rig's default passband
            // for this mode") -- verified against hamlib/include/hamlib/rig.h. RadioState has no
            // passband field, so this is always what ScanlineStudio asks for.
            await SendSetCommandAsync($"M {token} 0", ct).ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public async Task SetPttAsync(bool tx, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _requestLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            await SendSetCommandAsync($"T {(tx ? 1 : 0)}", ct).ConfigureAwait(false);
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
            throw new TimeoutException($"Connecting to rigctld timed out after {_connectTimeout}.");
        }

        Capabilities = await ProbeCapabilitiesAsync(ct).ConfigureAwait(false);
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

        // rigctld's backward-compatible "easy" command set has no signal-strength query -- that's an
        // extended-level command, explicitly out of v1 scope (spec/04-rigctld.md's Non-goals).
        return caps;
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
        if (!int.TryParse(codeText, out var code) || code != 0)
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _transport.DisposeAsync().ConfigureAwait(false);
        _requestLock.Dispose();
    }
}
