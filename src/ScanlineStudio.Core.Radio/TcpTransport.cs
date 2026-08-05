using System.Net.Sockets;
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
public sealed class TcpTransport : IRadioTransport
{
    private const int ReadBufferSize = 4096;

    private readonly string _host;
    private readonly int _port;
    private readonly byte[] _readBuffer = new byte[ReadBufferSize];
    private int _readOffset;
    private int _readLength;

    private TcpClient? _client;
    private NetworkStream? _stream;
    private bool _disposed;

    public TcpTransport(string host, int port)
    {
        _host = host;
        _port = port;
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
        try
        {
            await client.ConnectAsync(_host, _port, ct).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        _client = client;
        _stream = client.GetStream();
        _readOffset = 0;
        _readLength = 0;
    }

    public Task CloseAsync()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
        return Task.CompletedTask;
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stream is null)
        {
            throw new InvalidOperationException("Transport is not open -- call OpenAsync first.");
        }

        await _stream.WriteAsync(data, ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<byte> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stream is null)
        {
            throw new InvalidOperationException("Transport is not open -- call OpenAsync first.");
        }

        var stream = _stream;

        while (true)
        {
            if (_readOffset >= _readLength)
            {
                var bytesRead = await stream.ReadAsync(_readBuffer.AsMemory(), ct).ConfigureAwait(false);
                if (bytesRead == 0)
                {
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
}
