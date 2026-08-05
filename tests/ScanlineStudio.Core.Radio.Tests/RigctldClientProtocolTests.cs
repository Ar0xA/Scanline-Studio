using System.Runtime.CompilerServices;
using System.Text;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Core.Radio;
using ScanlineStudio.Core.Radio.Rigctld;

namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>Fixture-driven tests against <see cref="RigctldClientProtocol"/>, scripted via
/// <see cref="FakeRadioTransport"/>. Every wire-format detail exercised here (response framing,
/// mode tokens, PTT value range) was verified directly against a local Hamlib source clone
/// (`tests/rigctl_parse.c`, `src/misc.c`) rather than assumed from prose -- see spec/04-rigctld.md's
/// "Response framing" section -- so these are spec/Hamlib-source-derived fixtures, not a real
/// `rigctld` capture. <see cref="RigctldDummyRigIntegrationTests"/> is what retires that caveat.
///
/// Every scenario connects fresh, so every script starts with the capability-negotiation probe
/// (`f`, `m`, `t` issued once -- spec/04-rigctld.md's "Discovery and capability negotiation") before
/// whatever the test is actually targeting.</summary>
public class RigctldClientProtocolTests
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task PollAsync_FullCapabilities_ReturnsFullyPopulatedState()
    {
        var script = Script(
            "14074000", "USB", "0", "0", // probe: f, m (2 lines), t
            "14074000", "USB", "0", "0"); // poll: f, m (2 lines), t
        var transport = new FakeRadioTransport(script);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.Equal(14074000, state.FrequencyHz);
        Assert.Equal(RadioMode.Usb, state.Mode);
        Assert.False(state.IsTransmitting);
        Assert.Null(state.SignalStrengthDb);
        Assert.Equal(
            RadioCapabilities.ReadFrequency | RadioCapabilities.SetFrequency |
            RadioCapabilities.ReadMode | RadioCapabilities.SetMode | RadioCapabilities.PttControl,
            sut.Capabilities);
        Assert.Equal("f\nm\nt\nf\nm\nt\n", transport.WrittenText);
    }

    [Fact]
    public async Task PollAsync_ModeAndPttUnsupported_SkipsThoseCommands_AndDefaultsFieldsSafely()
    {
        var script = Script(
            "14074000", "RPRT -11", "RPRT -11", // probe: f ok, m unsupported, t unsupported
            "14074000"); // poll: only f -- m/t never issued since Capabilities lacks them
        var transport = new FakeRadioTransport(script);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.Equal(14074000, state.FrequencyHz);
        Assert.Equal(RadioMode.Unknown, state.Mode);
        Assert.False(state.IsTransmitting);
        Assert.Equal(RadioCapabilities.ReadFrequency | RadioCapabilities.SetFrequency, sut.Capabilities);
        Assert.Equal("f\nm\nt\nf\n", transport.WrittenText);
    }

    [Fact]
    public async Task PollAsync_ThrowsRadioProtocolException_WhenFrequencyCommandReturnsRprtError()
    {
        var script = Script(
            "14074000", "USB", "0", "0", // probe succeeds
            "RPRT -1"); // poll's 'f' now fails (e.g. rig powered off mid-session)
        var transport = new FakeRadioTransport(script);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);

        var ex = await Assert.ThrowsAsync<RadioProtocolException>(
            () => sut.PollAsync(CancellationToken.None));
        Assert.Contains("RPRT -1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PollAsync_ThrowsRadioProtocolException_WhenFrequencyResponseIsUnparseable()
    {
        var script = Script(
            "14074000", "USB", "0", "0",
            "not-a-number");
        var transport = new FakeRadioTransport(script);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);

        await Assert.ThrowsAsync<RadioProtocolException>(() => sut.PollAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("USB", RadioMode.Usb)]
    [InlineData("LSB", RadioMode.Lsb)]
    [InlineData("CW", RadioMode.Cw)]
    [InlineData("CWR", RadioMode.CwR)]
    [InlineData("CW-R", RadioMode.CwR)]
    [InlineData("AM", RadioMode.Am)]
    [InlineData("FM", RadioMode.Fm)]
    [InlineData("RTTY", RadioMode.Rtty)]
    [InlineData("RTTYR", RadioMode.RttyR)]
    [InlineData("PKTUSB", RadioMode.Data)]
    [InlineData("PKTLSB", RadioMode.DataR)]
    [InlineData("PKTFM", RadioMode.Pkt)]
    [InlineData("WFM", RadioMode.Unknown)] // a real Hamlib token with no RadioMode equivalent
    [InlineData("SOME-UNKNOWN-TOKEN", RadioMode.Unknown)] // not a real Hamlib token at all
    public async Task PollAsync_MapsModeToken(string token, RadioMode expected)
    {
        var script = Script(
            "14074000", token, "0", "0",
            "14074000", token, "0", "0");
        var transport = new FakeRadioTransport(script);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.Equal(expected, state.Mode);
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("2", true)] // RIG_PTT_ON_MIC
    [InlineData("3", true)] // RIG_PTT_ON_DATA
    public async Task PollAsync_TreatsAnyNonzeroPttValueAsTransmitting(string pttValue, bool expected)
    {
        var script = Script(
            "14074000", "USB", "0", pttValue,
            "14074000", "USB", "0", pttValue);
        var transport = new FakeRadioTransport(script);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.Equal(expected, state.IsTransmitting);
    }

    [Fact]
    public async Task SetFrequencyAsync_SendsFCommand_AndSucceedsOnRprtZero()
    {
        var script = Script("14074000", "USB", "0", "0", "RPRT 0");
        var transport = new FakeRadioTransport(script);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);

        await sut.SetFrequencyAsync(7074000, CancellationToken.None);

        Assert.Equal("f\nm\nt\nF 7074000\n", transport.WrittenText);
    }

    [Fact]
    public async Task SetFrequencyAsync_ThrowsRadioProtocolException_OnRprtError()
    {
        var script = Script("14074000", "USB", "0", "0", "RPRT -1");
        var transport = new FakeRadioTransport(script);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);

        await Assert.ThrowsAsync<RadioProtocolException>(
            () => sut.SetFrequencyAsync(7074000, CancellationToken.None));
    }

    [Fact]
    public async Task SetModeAsync_SendsMCommand_WithNormalPassbandSentinel()
    {
        var script = Script("14074000", "USB", "0", "0", "RPRT 0");
        var transport = new FakeRadioTransport(script);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);

        await sut.SetModeAsync(RadioMode.Lsb, CancellationToken.None);

        // Passband 0 = Hamlib's RIG_PASSBAND_NORMAL sentinel (verified against rig.h) -- ScanlineStudio's
        // domain model has no passband field, so this is always what's requested.
        Assert.Equal("f\nm\nt\nM LSB 0\n", transport.WrittenText);
    }

    [Fact]
    public void SetModeAsync_ThrowsArgumentOutOfRangeException_ForUnmappableMode()
    {
        var transport = new FakeRadioTransport(ReadOnlyMemory<byte>.Empty);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);

        // RadioMode.Unknown is a valid enum value but was never a real mode to begin with -- there is
        // no wire command that means "set the mode to unknown."
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => sut.SetModeAsync(RadioMode.Unknown, CancellationToken.None));
    }

    [Fact]
    public async Task SetPttAsync_SendsTCommand()
    {
        var script = Script("14074000", "USB", "0", "0", "RPRT 0");
        var transport = new FakeRadioTransport(script);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);

        await sut.SetPttAsync(true, CancellationToken.None);

        Assert.Equal("f\nm\nt\nT 1\n", transport.WrittenText);
    }

    [Fact]
    public async Task EnsureConnected_OnlyProbesOnce_AcrossMultipleCalls()
    {
        var script = Script(
            "14074000", "USB", "0", "0", // probe -- only ever sent once
            "14074000", "USB", "0", "0", // poll 1
            "14074000", "USB", "0", "0"); // poll 2
        var transport = new FakeRadioTransport(script);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);

        await sut.PollAsync(CancellationToken.None);
        await sut.PollAsync(CancellationToken.None);

        Assert.Equal("f\nm\nt\nf\nm\nt\nf\nm\nt\n", transport.WrittenText);
        Assert.True(transport.IsOpen);
    }

    [Fact]
    public async Task PollAsync_ThrowsTimeoutException_WhenConnectingTakesLongerThanConnectTimeout()
    {
        var transport = new NeverOpensTransport();
        var sut = new RigctldClientProtocol(transport, TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TimeoutException>(() => sut.PollAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DisposeAsync_ClosesTheUnderlyingTransport()
    {
        var transport = new FakeRadioTransport(ReadOnlyMemory<byte>.Empty);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);
        await transport.OpenAsync(CancellationToken.None);

        await sut.DisposeAsync();

        Assert.False(transport.IsOpen);
    }

    private static byte[] Script(params string[] lines) =>
        Encoding.ASCII.GetBytes(string.Join(string.Empty, lines.Select(static l => l + "\n")));

    private sealed class NeverOpensTransport : IRadioTransport
    {
        public bool IsOpen => false;

        public Task OpenAsync(CancellationToken ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct);

        public Task CloseAsync() => Task.CompletedTask;

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => Task.CompletedTask;

        public async IAsyncEnumerable<byte> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
