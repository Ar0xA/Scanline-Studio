using System.Runtime.CompilerServices;
using System.Text;
using Yoniq.Abstractions.Radio;

namespace Yoniq.Core.Radio;

/// <summary>
/// Test double for <see cref="IRadioTransport"/> — a reusable seam (lives in <c>src/</c>, not
/// <c>tests/</c>, mirroring <c>Yoniq.Core.Audio.FakeAudioEngine</c>'s placement), constructed with a
/// script of bytes to replay on <see cref="ReadAsync"/> and recording everything written via
/// <see cref="WriteAsync"/> for assertion.
///
/// Deliberately serves the scripted bytes in small internal chunks (<paramref name="chunkSize"/>,
/// default 7 — small and non-power-of-two on purpose, to force multiple refill cycles even for short
/// scripted lines) rather than handing out the whole script in one shot. <see cref="TcpTransport"/>'s
/// buffer-survival contract (see its own doc comment) is only worth having if something actually
/// exercises the multi-refill code path it protects — a fake that always has the entire script
/// pre-buffered would never need a second refill, and could pass every test while hiding a real
/// offset-tracking bug that only manifests when a real socket read returns fewer bytes than a caller
/// wanted. Enforces the same contract <see cref="TcpTransport"/> does: unconsumed bytes in the
/// internal buffer survive across separate <see cref="ReadAsync"/> enumerations, because that buffer
/// is instance state, never local to one enumeration.
/// </summary>
public sealed class FakeRadioTransport : IRadioTransport
{
    private readonly byte[] _replayBytes;
    private readonly int _chunkSize;
    private readonly List<byte> _written = [];
    private readonly byte[] _readBuffer;

    private int _replayPosition;
    private int _readOffset;
    private int _readLength;
    private bool _open;
    private bool _disposed;

    public FakeRadioTransport(ReadOnlyMemory<byte> replayBytes, int chunkSize = 7)
    {
        if (chunkSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSize), chunkSize, "Must be positive.");
        }

        _replayBytes = replayBytes.ToArray();
        _chunkSize = chunkSize;
        _readBuffer = new byte[chunkSize];
    }

    public bool IsOpen => _open;

    public IReadOnlyList<byte> WrittenBytes => _written;

    /// <summary>Convenience for asserting against rigctld's ASCII line commands -- not part of the
    /// buffer-survival contract, purely a test-readability helper.</summary>
    public string WrittenText => Encoding.ASCII.GetString(_written.ToArray());

    public Task OpenAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_open)
        {
            throw new InvalidOperationException("Transport is already open -- call CloseAsync first.");
        }

        _open = true;
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        _open = false;
        return Task.CompletedTask;
    }

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_open)
        {
            throw new InvalidOperationException("Transport is not open -- call OpenAsync first.");
        }

        _written.AddRange(data.Span.ToArray());
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<byte> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_open)
        {
            throw new InvalidOperationException("Transport is not open -- call OpenAsync first.");
        }

        while (true)
        {
            if (_readOffset >= _readLength)
            {
                if (_replayPosition >= _replayBytes.Length)
                {
                    throw new IOException(
                        "FakeRadioTransport: scripted replay bytes exhausted -- the test script didn't " +
                        "provide enough bytes for what the protocol under test tried to read.");
                }

                var bytesToCopy = Math.Min(_chunkSize, _replayBytes.Length - _replayPosition);
                Array.Copy(_replayBytes, _replayPosition, _readBuffer, 0, bytesToCopy);
                _replayPosition += bytesToCopy;
                _readOffset = 0;
                _readLength = bytesToCopy;

                // Genuinely yield here (not just a formality to satisfy the compiler) -- a real
                // socket read is always async, and a fake that completes every refill synchronously
                // could hide a caller bug that only shows up against real async scheduling.
                await Task.Yield();
            }

            ct.ThrowIfCancellationRequested();
            yield return _readBuffer[_readOffset++];
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _open = false;
        return ValueTask.CompletedTask;
    }
}
