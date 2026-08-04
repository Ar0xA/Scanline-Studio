using System.Text;
using Yoniq.Core.Radio;

namespace Yoniq.Core.Radio.Tests;

/// <summary>Pins <see cref="FakeRadioTransport"/>'s own buffer-survival contract -- the same contract
/// <see cref="TcpTransport"/> promises (see its doc comment) -- deterministically, using a small
/// chunk size chosen specifically to force a line break to land mid-chunk rather than exactly on a
/// chunk boundary.</summary>
public class FakeRadioTransportTests
{
    [Fact]
    public async Task ReadAsync_LeavesMidChunkLeftoverBytes_AvailableToTheNextSeparateEnumeration()
    {
        // "ab\ncd\n" with chunkSize 4: chunk 1 = ['a','b','\n','c'], chunk 2 = ['d','\n'].
        // The first line's '\n' lands at index 2 of chunk 1, leaving 'c' unconsumed mid-chunk.
        var transport = new FakeRadioTransport(Encoding.ASCII.GetBytes("ab\ncd\n"), chunkSize: 4);
        await transport.OpenAsync(CancellationToken.None);

        var first = await ReadLineAsync(transport);
        var second = await ReadLineAsync(transport);

        Assert.Equal("ab", first);
        Assert.Equal("cd", second);
    }

    [Fact]
    public async Task WriteAsync_RecordsWrittenBytes()
    {
        var transport = new FakeRadioTransport(Encoding.ASCII.GetBytes("ignored\n"));
        await transport.OpenAsync(CancellationToken.None);

        await transport.WriteAsync(Encoding.ASCII.GetBytes("f\n"), CancellationToken.None);
        await transport.WriteAsync(Encoding.ASCII.GetBytes("F 14074000\n"), CancellationToken.None);

        Assert.Equal("f\nF 14074000\n", transport.WrittenText);
    }

    [Fact]
    public async Task ReadAsync_ThrowsIOException_WhenScriptedBytesAreExhausted()
    {
        var transport = new FakeRadioTransport(Encoding.ASCII.GetBytes("ab"));
        await transport.OpenAsync(CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var _ in transport.ReadAsync(CancellationToken.None))
            {
                // Only 2 bytes were scripted -- reading a 3rd must fail loudly, not hang or wrap.
            }
        });
    }

    [Fact]
    public async Task OpenAsync_Throws_WhenAlreadyOpen()
    {
        var transport = new FakeRadioTransport(ReadOnlyMemory<byte>.Empty);
        await transport.OpenAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.OpenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WriteAsync_Throws_WhenNotOpen()
    {
        var transport = new FakeRadioTransport(ReadOnlyMemory<byte>.Empty);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => transport.WriteAsync(Encoding.ASCII.GetBytes("f\n"), CancellationToken.None));
    }

    [Fact]
    public async Task DisposeAsync_CausesSubsequentCalls_ToThrowObjectDisposedException()
    {
        var transport = new FakeRadioTransport(ReadOnlyMemory<byte>.Empty);
        await transport.OpenAsync(CancellationToken.None);
        await transport.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.OpenAsync(CancellationToken.None));
    }

    private static async Task<string> ReadLineAsync(FakeRadioTransport transport)
    {
        var bytes = new List<byte>();
        await foreach (var b in transport.ReadAsync(CancellationToken.None))
        {
            if (b == (byte)'\n')
            {
                break;
            }

            bytes.Add(b);
        }

        return Encoding.ASCII.GetString(bytes.ToArray());
    }
}
