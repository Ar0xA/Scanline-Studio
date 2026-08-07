using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Core.Logbook;

/// <summary>See <see cref="IGridTrackerStreamer"/>. Sends a WSJT-X network-protocol
/// <c>LoggedADIF</c> (type 12) UDP datagram — wire format verified directly against WSJT-X's own
/// <c>NetworkMessage.hpp</c> (schema 3, current as of this writing), not inferred from GridTracker's
/// own docs (which don't document the wire format at all):
///
/// <code>
/// quint32 magic  = 0xadbccbda   (big-endian -- QDataStream's default byte order, never overridden)
/// quint32 schema = 2            (schema 2 is the lowest/safest baseline any GridTracker version
///                                 accepts; this class never sends/receives Heartbeat, so there is
///                                 no schema negotiation to piggy-back a higher number on)
/// quint32 type   = 12           (LoggedADIF)
/// utf8    id                    (Qt QByteArray framing: big-endian quint32 byte-length prefix,
///                                 then raw UTF-8 bytes -- 0xFFFFFFFF length would mean "null
///                                 string", never emitted here since id/adifText are always non-null)
/// utf8    adifText
/// </code>
///
/// A malformed datagram fails <b>silently</b> on the receiving end -- GridTracker simply never
/// shows the QSO, with no error surfaced anywhere on this side or that one -- which is why this
/// specific encoder got an <c>auditor</c> pass (CLAUDE.md §7's "buffer/encoding logic" trigger)
/// even though it isn't DSP.</summary>
public sealed partial class GridTrackerStreamer : IGridTrackerStreamer
{
    private const uint Magic = 0xadbccbda;
    private const uint Schema = 2;
    private const uint LoggedAdifMessageType = 12;

    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<GridTrackerStreamer> _logger;

    public GridTrackerStreamer(ISettingsStore settingsStore, ILogger<GridTrackerStreamer> logger)
    {
        _settingsStore = settingsStore;
        _logger = logger;
    }

    /// <summary>Best-effort per <see cref="IGridTrackerStreamer"/>'s own contract: catches every
    /// failure mode this call can realistically hit (a corrupt settings file, an invalid
    /// persisted host/port, a DNS/socket failure) and returns <c>false</c> rather than throwing,
    /// so a GridTracker misconfiguration or outage never blocks the caller from persisting the QSO
    /// locally. Deliberately does <b>not</b> catch <see cref="OperationCanceledException"/> — a
    /// caller-requested cancellation should propagate like any other async .NET API, not be
    /// silently reported as "failed to send."</summary>
    public async Task<bool> SendLoggedQsoAsync(string adifText, CancellationToken ct = default)
    {
        var host = GridTrackerStreamingSettings.DefaultHost;
        var port = GridTrackerStreamingSettings.DefaultPort;

        try
        {
            var settings = await GridTrackerStreamingSettings.ResolveAsync(_settingsStore, ct).ConfigureAwait(false);
            if (settings.Enabled != true)
            {
                return false;
            }

            host = settings.Host is { Length: > 0 } ? settings.Host : GridTrackerStreamingSettings.DefaultHost;
            port = settings.Port is > 0 and <= 65535 ? settings.Port.Value : GridTrackerStreamingSettings.DefaultPort;
            var clientId = settings.ClientId is { Length: > 0 } ? settings.ClientId : GridTrackerStreamingSettings.DefaultClientId;
            var datagram = BuildLoggedAdifDatagram(clientId, adifText);

            using var client = new UdpClient();
            await client.SendAsync(datagram, host, port, ct).ConfigureAwait(false);
            Log.DatagramSent(_logger, host, port);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.SendFailed(_logger, host, port, ex);
            return false;
        }
    }

    /// <summary>Exposed <c>internal</c> (see <c>AssemblyInfo.cs</c>'s <c>InternalsVisibleTo</c>) so
    /// tests can assert the exact byte layout without a real socket.</summary>
    internal static byte[] BuildLoggedAdifDatagram(string clientId, string adifText)
    {
        using var stream = new MemoryStream();
        WriteUInt32BigEndian(stream, Magic);
        WriteUInt32BigEndian(stream, Schema);
        WriteUInt32BigEndian(stream, LoggedAdifMessageType);
        WriteUtf8Field(stream, clientId);
        WriteUtf8Field(stream, adifText);
        return stream.ToArray();
    }

    private static void WriteUInt32BigEndian(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUtf8Field(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteUInt32BigEndian(stream, (uint)bytes.Length);
        stream.Write(bytes);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Sent logged QSO to GridTracker at {Host}:{Port}")]
        public static partial void DatagramSent(ILogger logger, string host, int port);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to send logged QSO to GridTracker at {Host}:{Port}")]
        public static partial void SendFailed(ILogger logger, string host, int port, Exception ex);
    }
}
