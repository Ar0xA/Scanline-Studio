using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Core.Logbook;

/// <summary>See <see cref="IAdifUdpStreamer"/>. Sends a WSJT-X network-protocol <c>LoggedADIF</c>
/// (type 12) UDP datagram — wire format verified directly against WSJT-X's own
/// <c>NetworkMessage.hpp</c> (schema 3, current as of this writing), not inferred from any single
/// receiving program's own docs (none of GridTracker/N1MM/Log4OM fully document the wire format
/// themselves):
///
/// <code>
/// quint32 magic  = 0xadbccbda   (big-endian -- QDataStream's default byte order, never overridden)
/// quint32 schema = 2            (schema 2 is the lowest/safest baseline any receiver accepts; this
///                                 class never sends/receives Heartbeat, so there is no schema
///                                 negotiation to piggy-back a higher number on)
/// quint32 type   = 12           (LoggedADIF)
/// utf8    id                    (Qt QByteArray framing: big-endian quint32 byte-length prefix,
///                                 then raw UTF-8 bytes -- 0xFFFFFFFF length would mean "null
///                                 string", never emitted here since id/adifText are always non-null)
/// utf8    adifText
/// </code>
///
/// A malformed datagram fails <b>silently</b> on the receiving end -- the destination program simply
/// never shows the QSO, with no error surfaced anywhere on this side or that one -- which is why
/// this specific encoder got an <c>auditor</c> pass (CLAUDE.md §7's "buffer/encoding logic" trigger)
/// even though it isn't DSP.
///
/// Generalized (2026-08-15) from a GridTracker-only streamer to fan the SAME datagram out to every
/// configured, enabled <see cref="AdifUdpDestination"/> -- see <see cref="IAdifUdpStreamer"/>'s own
/// doc comment for why one wire format serves GridTracker/N1MM/Log4OM/anything else that speaks it.
/// Each destination gets its own <see cref="UdpClient"/>, constructed with that destination's
/// resolved <see cref="System.Net.Sockets.AddressFamily"/> (auditor code-review finding: a bare
/// parameterless <see cref="UdpClient"/> is IPv4-only, not dual-mode, so an IPv6-resolving host --
/// "localhost" itself, on Windows and on most glibc Linux setups -- would otherwise throw on send
/// and fail silently, which is exactly the failure mode the class's own "malformed datagram fails
/// silently" note above already warns about, just one layer earlier). Each destination also gets
/// its own linked cancellation source with a <see cref="SendTimeout"/> -- a raw UDP send itself
/// can't hang, but resolving a hostname (as opposed to an IP literal) can, and
/// <see cref="Dns.GetHostAddressesAsync(string,CancellationToken)"/> is used explicitly (rather than
/// the string-hostname overload of <c>UdpClient.SendAsync</c>, whose DNS-resolve step is not
/// reliably cancellable) wrapped in <see cref="Task.WaitAsync(CancellationToken)"/> so the per-
/// destination stall this method's OWN CALLER observes is genuinely bounded even on a platform/path
/// where the underlying OS resolver call itself can't be interrupted (auditor code-review finding:
/// .NET's non-Windows-async-resolver fallback runs a blocking <c>getaddrinfo</c> on a pool thread).
/// Sends run concurrently via <see cref="Task.WhenAll{TResult}(IEnumerable{Task{TResult}})"/> -- one
/// slow or unreachable destination bounds total time to itself, not to the sum of every
/// destination.</summary>
public sealed partial class AdifUdpStreamer : IAdifUdpStreamer
{
    private const uint Magic = 0xadbccbda;
    private const uint Schema = 2;
    private const uint LoggedAdifMessageType = 12;

    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(2);

    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<AdifUdpStreamer> _logger;

    public AdifUdpStreamer(ISettingsStore settingsStore, ILogger<AdifUdpStreamer> logger)
    {
        _settingsStore = settingsStore;
        _logger = logger;
    }

    /// <summary>Best-effort per <see cref="IAdifUdpStreamer"/>'s own contract: a settings-load
    /// failure, a per-destination DNS/socket failure, or a per-destination timeout all resolve to
    /// "that destination didn't get the QSO," never a thrown exception -- so a misconfiguration or
    /// outage on any one destination never blocks the caller from persisting the QSO locally or from
    /// reaching the other destinations. Deliberately does <b>not</b> catch a genuine CALLER-requested
    /// cancellation via <paramref name="ct"/> -- that propagates like any other async .NET API, not
    /// silently reported as "failed to send" (see <see cref="SendToDestinationAsync"/>'s own
    /// cancellation-vs-timeout discrimination).</summary>
    public async Task<AdifUdpSendResult> SendLoggedQsoAsync(string adifText, CancellationToken ct = default)
    {
        IReadOnlyList<AdifUdpDestination> destinations;
        string clientId;
        try
        {
            var settings = await AdifUdpStreamingSettings.ResolveAsync(_settingsStore, ct).ConfigureAwait(false);
            destinations = settings.Destinations ?? [];
            clientId = settings.ClientId is { Length: > 0 } ? settings.ClientId : AdifUdpStreamingSettings.DefaultClientId;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.SettingsLoadFailed(_logger, ex);
            return new AdifUdpSendResult(0, 0);
        }

        // EnabledCount is every Enabled==true entry, full stop -- including a malformed one (empty
        // Host / out-of-range Port). Auditor code-review finding: an earlier version filtered
        // malformed-but-enabled entries out BEFORE computing EnabledCount, which silently hid a
        // typo'd destination behind a falsely "complete" N/N count (the user's other, working
        // destination alone would read as "sent to 1/1" instead of the true "1/2, one destination
        // is broken"). SendToDestinationAsync itself now validates and reports per destination.
        var enabled = destinations.Where(d => d.Enabled == true).ToList();
        if (enabled.Count == 0)
        {
            return new AdifUdpSendResult(0, 0);
        }

        var datagram = BuildLoggedAdifDatagram(clientId, adifText);
        var results = await Task.WhenAll(enabled.Select(d => SendToDestinationAsync(d, datagram, ct))).ConfigureAwait(false);
        return new AdifUdpSendResult(results.Count(sent => sent), enabled.Count);
    }

    private async Task<bool> SendToDestinationAsync(AdifUdpDestination destination, byte[] datagram, CancellationToken ct)
    {
        var name = destination.Name is { Length: > 0 }
            ? destination.Name
            : destination.Host is { Length: > 0 } ? destination.Host : "(unnamed destination)";
        if (destination.Host is not { Length: > 0 } host || destination.Port is not (> 0 and <= 65535))
        {
            Log.InvalidDestination(_logger, name);
            return false;
        }

        var port = destination.Port!.Value;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(SendTimeout);

        try
        {
            // .WaitAsync bounds how long THIS AWAIT can observe the resolve taking, even on a
            // platform/path where the underlying OS resolver call itself doesn't reliably honor
            // timeoutCts.Token (auditor code-review finding: .NET's non-Windows-async-resolver
            // fallback path runs a blocking getaddrinfo on a pool thread that isn't interruptible --
            // the token only makes the NEXT awaited step throw promptly, which .WaitAsync gives us
            // here without waiting for that background call to ever actually finish).
            var addresses = await Dns.GetHostAddressesAsync(host, timeoutCts.Token).WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            if (addresses.Length == 0)
            {
                Log.SendFailed(_logger, name, host, port, new SocketException((int)SocketError.HostNotFound));
                return false;
            }

            // The address family must match the socket's -- auditor code-review finding: a bare
            // `new UdpClient()` is IPv4-only (AddressFamily.InterNetwork, not dual-mode; only an
            // IPv6 socket can be dual-mode), so an IPv6-first resolution (which "localhost" itself
            // produces on Windows and on most glibc Linux setups, RFC 6724 preference) would throw
            // ArgumentException on SendAsync and get silently swallowed by the catch below --
            // "same-machine external logger" is the single most likely real configuration for this
            // feature, so this was not a corner case.
            var address = addresses[0];
            using var client = new UdpClient(address.AddressFamily);
            await client.SendAsync(datagram, new IPEndPoint(address, port), timeoutCts.Token).ConfigureAwait(false);
            Log.DatagramSent(_logger, name, host, port);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Only the linked timeout could have fired here, since a genuine caller cancellation
            // (ct itself) would have set ct.IsCancellationRequested -- see this method's own
            // cancellation-vs-timeout contract in the class doc comment.
            Log.SendTimedOut(_logger, name, host, port);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.SendFailed(_logger, name, host, port, ex);
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
        [LoggerMessage(Level = LogLevel.Information, Message = "Sent logged QSO to {Name} ({Host}:{Port})")]
        public static partial void DatagramSent(ILogger logger, string name, string host, int port);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to send logged QSO to {Name} ({Host}:{Port})")]
        public static partial void SendFailed(ILogger logger, string name, string host, int port, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Timed out sending logged QSO to {Name} ({Host}:{Port})")]
        public static partial void SendTimedOut(ILogger logger, string name, string host, int port);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading ADIF UDP streaming settings failed")]
        public static partial void SettingsLoadFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Destination {Name} is enabled but missing a valid Host/Port -- skipped")]
        public static partial void InvalidDestination(ILogger logger, string name);
    }
}
