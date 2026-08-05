using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Core.Radio.Rigctld;

namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>
/// Real interop test: spins up an actual `rigctld` process (Hamlib's own daemon) against Hamlib's
/// hardware-free "Dummy" rig backend (model 1 -- confirmed against a local Hamlib source clone,
/// `riglist.h`'s `RIG_MODEL_DUMMY`/`port_type = RIG_PORT_NONE`, and manually smoke-tested: `rigctld -m 1`
/// genuinely needs no hardware), and drives this project's own <see cref="TcpTransport"/>/
/// <see cref="RigctldClientProtocol"/> against it over a real loopback socket. This is what retires the
/// "unverified-grammar" caveat <see cref="RigctldClientProtocolTests"/>' hand-derived fixtures carry --
/// real interop against real Hamlib code, not fixtures re-derived from reading source a second time.
///
/// Best-effort: skipped (not failed) when `rigctld` isn't on PATH, matching spec/04-rigctld.md's own
/// "optionally cross-checked... if Hamlib is available" precedent. xUnit 2.x has no built-in runtime
/// skip without an extra package (`Assert.Skip` is an xUnit v3 feature) -- deliberately returning early
/// rather than adding a dependency for one test file; these show as "passed," not "skipped," when
/// `rigctld` is unavailable, which is a cosmetic gap, not a functional one (they never fail either way).
/// </summary>
public class RigctldDummyRigIntegrationTests
{
    private static readonly Lazy<bool> RigctldIsAvailable = new(CheckRigctldAvailable);

    [Fact]
    public async Task PollAsync_ReadsTheDummyRigsRealDefaultState()
    {
        if (!RigctldIsAvailable.Value)
        {
            return;
        }

        await using var dummy = await DummyRigctldProcess.StartAsync();
        var transport = new TcpTransport("127.0.0.1", dummy.Port);
        var sut = new RigctldClientProtocol(transport, TimeSpan.FromSeconds(5));

        var state = await sut.PollAsync(CancellationToken.None);

        // Hamlib's Dummy backend's real shipped defaults (rigs/dummy/dummy.c) -- not invented.
        Assert.Equal(145_000_000, state.FrequencyHz);
        Assert.Equal(RadioMode.Fm, state.Mode);

        await sut.DisposeAsync();
    }

    [Fact]
    public async Task SetFrequencyAsync_ThenPollAsync_ReflectsTheRealChange()
    {
        if (!RigctldIsAvailable.Value)
        {
            return;
        }

        await using var dummy = await DummyRigctldProcess.StartAsync();
        var transport = new TcpTransport("127.0.0.1", dummy.Port);
        var sut = new RigctldClientProtocol(transport, TimeSpan.FromSeconds(5));

        await sut.SetFrequencyAsync(7_074_000, CancellationToken.None);
        var state = await sut.PollAsync(CancellationToken.None);

        Assert.Equal(7_074_000, state.FrequencyHz);

        await sut.DisposeAsync();
    }

    [Fact]
    public async Task SetModeAsync_ThenPollAsync_ReflectsTheRealChange()
    {
        if (!RigctldIsAvailable.Value)
        {
            return;
        }

        await using var dummy = await DummyRigctldProcess.StartAsync();
        var transport = new TcpTransport("127.0.0.1", dummy.Port);
        var sut = new RigctldClientProtocol(transport, TimeSpan.FromSeconds(5));

        await sut.SetModeAsync(RadioMode.Usb, CancellationToken.None);
        var state = await sut.PollAsync(CancellationToken.None);

        Assert.Equal(RadioMode.Usb, state.Mode);

        await sut.DisposeAsync();
    }

    [Fact]
    public async Task Capabilities_PttUnsupportedOnTheDummyRig_IsProbedCorrectly()
    {
        if (!RigctldIsAvailable.Value)
        {
            return;
        }

        await using var dummy = await DummyRigctldProcess.StartAsync();
        var transport = new TcpTransport("127.0.0.1", dummy.Port);
        var sut = new RigctldClientProtocol(transport, TimeSpan.FromSeconds(5));

        var state = await sut.PollAsync(CancellationToken.None);

        // Real, verified Dummy-backend behavior (manually smoke-tested against a real rigctld
        // process): 't' (get_ptt) returns "RPRT -11" (not implemented) on this backend -- the exact
        // capability-absence path RigctldClientProtocolTests exercises with a hand-derived fixture.
        Assert.False(sut.Capabilities.HasFlag(RadioCapabilities.PttControl));
        Assert.False(state.IsTransmitting);

        await sut.DisposeAsync();
    }

    private static bool CheckRigctldAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("rigctld", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            process?.WaitForExit(2000);
            return process is { ExitCode: 0 };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Owns a real `rigctld -m 1` (Hamlib's Dummy rig backend) child process bound to a
    /// dynamically-chosen loopback port, torn down on dispose.</summary>
    private sealed class DummyRigctldProcess : IAsyncDisposable
    {
        private readonly Process _process;

        private DummyRigctldProcess(Process process, int port)
        {
            _process = process;
            Port = port;
        }

        public int Port { get; }

        public static async Task<DummyRigctldProcess> StartAsync()
        {
            var port = GetFreeLoopbackPort();
            var process = Process.Start(new ProcessStartInfo("rigctld", $"-m 1 -t {port} -T 127.0.0.1")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException("Failed to start rigctld.");

            await WaitUntilAcceptingConnectionsAsync(port).ConfigureAwait(false);
            return new DummyRigctldProcess(process, port);
        }

        private static int GetFreeLoopbackPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static async Task WaitUntilAcceptingConnectionsAsync(int port)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var probe = new TcpClient();
                    await probe.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                    return;
                }
                catch (SocketException)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
                }
            }

            throw new TimeoutException($"rigctld never started accepting connections on port {port}.");
        }

        public async ValueTask DisposeAsync()
        {
            if (_process.HasExited)
            {
                _process.Dispose();
                return;
            }

            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().ConfigureAwait(false);
            _process.Dispose();
        }
    }
}
