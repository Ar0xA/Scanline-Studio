using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Radio;

/// <summary>
/// See spec/02-radio-layer.md, spec/04-rigctld.md. <see cref="IRadioTransport"/> over a plain TCP
/// socket — used by the rigctld and (later) flrig backends, never by call-based backends
/// (linked Hamlib, OmniRig), which don't implement <see cref="IRadioTransport"/> at all.
///
/// Implements <see cref="IRadioTransport"/>'s buffer-survival contract by construction, not by
/// convention: the internal read buffer (<c>_readBuffer</c>/<c>_readOffset</c>/<c>_readLength</c>)
/// is instance state, never local to a single <see cref="ReadAsync"/> enumeration. If a caller
/// disposes its enumerator mid-buffer (e.g. it read a line terminator and stopped, or timed out),
/// whatever bytes are left unconsumed in <c>_readBuffer</c> are exactly where the next
/// <see cref="ReadAsync"/> call's iterator picks back up — there is nothing to "restore," the state
/// was never lost. Single-consumer only, per the interface contract: concurrent enumeration would
/// race on this same instance state and is undefined, deliberately not guarded against here.
///
/// A read returning zero bytes means the remote end closed the connection — surfaced as an
/// <see cref="IOException"/> (a transport-level failure, per spec/04-rigctld.md's error taxonomy),
/// not a silent end of enumeration, so <c>RadioController</c>'s backoff/reconnect logic actually
/// sees it.
/// </summary>
public sealed partial class TcpTransport : IRadioTransport
{
    private const int ReadBufferSize = 4096;

    private readonly string _host;
    private readonly int _port;
    private readonly ILogger _logger;
    private readonly byte[] _readBuffer = new byte[ReadBufferSize];
    private int _readOffset;
    private int _readLength;

    private TcpClient? _client;
    private NetworkStream? _stream;
    private bool _disposed;

    // Optional, defaulting to a no-op logger: constructed via `new` in RigctldProtocolFactory,
    // not through DI. RigctldProtocolFactory does pass its own real ILogger through today.
    public TcpTransport(string host, int port, ILogger? logger = null)
    {
        _host = host;
        _port = port;
        _logger = logger ?? NullLogger.Instance;
    }

    public bool IsOpen => _stream is not null;

    public async Task OpenAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsOpen)
        {
            throw new InvalidOperationException("Transport is already open -- call CloseAsync first.");
        }

        var client = new TcpClient();
        NetworkStream stream;
        try
        {
            await client.ConnectAsync(_host, _port, ct).ConfigureAwait(false);
            // Inside the same try as ConnectAsync deliberately: GetStream throws
            // InvalidOperationException if the peer reset between accept and here. Assigning _client
            // before this call left a live, connected TcpClient owned by a transport whose IsOpen
            // reads false -- and RigctldClientProtocol.EnsureConnectedAsync retries OpenAsync on the
            // same instance, so the next attempt overwrote the field and leaked the socket outright.
            stream = client.GetStream();
        }
        catch (Exception ex)
        {
            // Cleanup-and-rethrow, not a swallow -- the real failure is logged by RadioController,
            // which owns the retry/backoff decision. Debug here just attributes it to this step.
            Log.ConnectFailed(_logger, _host, _port, ex);
            client.Dispose();
            throw;
        }

        _client = client;
        _stream = stream;
        _readOffset = 0;
        _readLength = 0;
        Log.Connected(_logger, _host, _port);
    }

    public Task CloseAsync()
    {
        AbortConnection();
        return Task.CompletedTask;
    }

    /// <summary>Single place that tears the socket down and resets the read buffer, so
    /// <see cref="IsOpen"/> and the buffer state can never disagree. Safe to call when already
    /// closed.</summary>
    private void AbortConnection()
    {
        var stream = _stream;
        var client = _client;
        _stream = null;
        _client = null;
        _readOffset = 0;
        _readLength = 0;
        stream?.Dispose();
        client?.Dispose();
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Captured into a local, same as ReadAsync: AbortConnection (a cancelled read on the read side,
        // CloseAsync, or DisposeAsync during shutdown) nulls _stream, so re-reading the field on the
        // write line below would turn this guarded InvalidOperationException into a
        // NullReferenceException instead.
        var stream = _stream;
        if (stream is null)
        {
            throw new InvalidOperationException("Transport is not open -- call OpenAsync first.");
        }

        try
        {
            await stream.WriteAsync(data, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Same reasoning as the read side: a cancelled write either half-wrote a command (the peer
            // sees a truncated line) or fully wrote one whose response nobody will ever read. Both leave
            // the request/response stream desynced, and a byte transport cannot resynchronize it -- kill
            // the connection so the next EnsureConnectedAsync reopens instead of silently misreading.
            AbortConnection();
            throw;
        }
    }

    public async IAsyncEnumerable<byte> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var stream = _stream;
        if (stream is null)
        {
            throw new InvalidOperationException("Transport is not open -- call OpenAsync first.");
        }

        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                // Abort here too, not only in the stream.ReadAsync catch below. THIS is the branch a
                // cancel hits whenever a response line (or, for `m`, the whole second line) is already
                // sitting in _readBuffer being drained a byte at a time -- RigctldClientProtocol opens a
                // fresh enumeration per line, so that state is routine, not exceptional. Throwing without
                // aborting left the unconsumed tail in _readBuffer with _stream still non-null (IsOpen
                // true, so EnsureConnectedAsync skips the reopen): the next ReadLineAsync would return
                // that tail as its own response and every later poll would be one response behind --
                // the exact silent, permanent desync IRadioTransport's contract exists to prevent.
                AbortConnection();
                ct.ThrowIfCancellationRequested();
            }

            if (_readOffset >= _readLength)
            {
                int bytesRead;
                try
                {
                    bytesRead = await stream.ReadAsync(_readBuffer.AsMemory(), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // A cancel landing here is NOT the benign "caller got its line and stopped" case
                    // the buffer-survival contract (this class's own doc comment) is written for. The
                    // request whose response was being read is already on the wire, so its bytes are
                    // still inbound (or partly sitting in _readBuffer): resuming would hand the NEXT
                    // caller the previous command's tail. For rigctld's line protocol that desync is
                    // permanent and silent -- every later poll parses the previous response, yielding a
                    // plausible-looking but permanently stale frequency/PTT readback, with
                    // RadioController never seeing a transport error to reconnect on. A byte transport
                    // cannot resynchronize a request/response stream, so the connection is killed
                    // instead: the next EnsureConnectedAsync reopens it and OpenAsync resets the
                    // buffer, turning a silent corruption into a loud, self-healing reconnect.
                    AbortConnection();
                    throw;
                }

                if (bytesRead == 0)
                {
                    // Abort, don't merely throw: leaving _stream non-null after the peer closed makes
                    // IsOpen lie, and IsOpen is exactly what RigctldClientProtocol.EnsureConnectedAsync
                    // uses to decide whether to reopen -- so a later Set*Async on this same protocol
                    // instance would skip the reopen and write into a dead socket. Same reasoning as
                    // the cancellation path above.
                    AbortConnection();
                    throw new IOException("rigctld connection closed by remote host.");
                }

                _readOffset = 0;
                _readLength = bytesRead;
            }

            yield return _readBuffer[_readOffset++];
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await CloseAsync().ConfigureAwait(false);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "TCP connect failed: {Host}:{Port}")]
        public static partial void ConnectFailed(ILogger logger, string host, int port, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "TCP connected: {Host}:{Port}")]
        public static partial void Connected(ILogger logger, string host, int port);
    }
}
