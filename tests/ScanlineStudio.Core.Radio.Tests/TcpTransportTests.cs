using System.Net;
using System.Net.Sockets;
using System.Text;
using ScanlineStudio.Core.Radio;

namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>Round-trips <see cref="TcpTransport"/> against a real loopback socket -- a sanity check
/// that the real implementation behaves like <see cref="FakeRadioTransportTests"/> already pins
/// deterministically for the fake.</summary>
public class TcpTransportTests
{
    [Fact]
    public async Task OpenAsync_ConnectsToListener_AndWriteAsync_DeliversBytes()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptTcpClientAsync();

        await using var transport = new TcpTransport("127.0.0.1", port);
        await transport.OpenAsync(CancellationToken.None);
        Assert.True(transport.IsOpen);

        using var serverClient = await acceptTask;
        await using var serverStream = serverClient.GetStream();

        await transport.WriteAsync(Encoding.ASCII.GetBytes("f\n"), CancellationToken.None);

        var buffer = new byte[16];
        var read = await serverStream.ReadAsync(buffer, CancellationToken.None);
        Assert.Equal("f\n", Encoding.ASCII.GetString(buffer, 0, read));

        listener.Stop();
    }

    [Fact]
    public async Task ReadAsync_LeavesMidChunkLeftoverBytes_AvailableToTheNextSeparateEnumeration()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptTcpClientAsync();

        await using var transport = new TcpTransport("127.0.0.1", port);
        await transport.OpenAsync(CancellationToken.None);

        using var serverClient = await acceptTask;
        await using var serverStream = serverClient.GetStream();

        // Written as a single call so it is very likely to arrive as one socket-level read on
        // loopback, exercising the same mid-buffer-leftover path FakeRadioTransportTests pins
        // deterministically -- see TcpTransport's own doc comment for why this matters.
        await serverStream.WriteAsync(Encoding.ASCII.GetBytes("ab\ncd\n"), CancellationToken.None);
        await serverStream.FlushAsync(CancellationToken.None);

        var first = await ReadLineAsync(transport);
        var second = await ReadLineAsync(transport);

        Assert.Equal("ab", first);
        Assert.Equal("cd", second);

        listener.Stop();
    }

    [Fact]
    public async Task ReadAsync_ThrowsIOException_WhenRemoteClosesConnection()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptTcpClientAsync();

        await using var transport = new TcpTransport("127.0.0.1", port);
        await transport.OpenAsync(CancellationToken.None);

        using (var serverClient = await acceptTask)
        {
            // Closing immediately -- the remote end hangs up with nothing sent.
        }

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var _ in transport.ReadAsync(CancellationToken.None))
            {
            }
        });

        // Regression test for chunk 3b round 2's finding F6: this used to leave _stream non-null
        // after the peer closed, so IsOpen kept reading true on a dead socket -- and IsOpen is
        // exactly what RigctldClientProtocol.EnsureConnectedAsync uses to decide whether to reopen,
        // so a later Set*Async on this same protocol instance would skip the reopen and write into a
        // dead socket.
        Assert.False(transport.IsOpen);

        listener.Stop();
    }

    [Fact]
    public async Task ReadAsync_CancelledMidRead_AbortsConnection_RatherThanDesyncingTheBuffer()
    {
        // Regression test for chunk 3b round 1's blocker 2: a cancel landing while stream.ReadAsync was
        // in flight used to leave the socket connected with the read cursor exactly where the in-flight
        // response's bytes would land -- so the NEXT ReadAsync call silently parsed a stale prior
        // response instead of the new one, with no exception ever raised. TcpTransport now aborts the
        // connection on a cancelled read instead: the failure is loud (IsOpen goes false), and the next
        // caller must reopen rather than silently misreading.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptTcpClientAsync();

        await using var transport = new TcpTransport("127.0.0.1", port);
        await transport.OpenAsync(CancellationToken.None);
        using var serverClient = await acceptTask;

        using var cts = new CancellationTokenSource();
        var readTask = Task.Run(async () =>
        {
            await foreach (var _ in transport.ReadAsync(cts.Token))
            {
            }
        });

        // No bytes are ever sent -- the read is guaranteed to still be blocked in the underlying socket
        // read when cancelled below, not racing a real response.
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
        Assert.False(transport.IsOpen);

        listener.Stop();
    }

    [Fact]
    public async Task OpenAsync_Throws_WhenAlreadyOpen()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptTcpClientAsync();

        await using var transport = new TcpTransport("127.0.0.1", port);
        await transport.OpenAsync(CancellationToken.None);
        using var serverClient = await acceptTask;

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.OpenAsync(CancellationToken.None));

        listener.Stop();
    }

    [Fact]
    public async Task WriteAsync_Throws_WhenNotOpen()
    {
        await using var transport = new TcpTransport("127.0.0.1", 0);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => transport.WriteAsync(Encoding.ASCII.GetBytes("f\n"), CancellationToken.None));
    }

    private static async Task<string> ReadLineAsync(TcpTransport transport)
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
