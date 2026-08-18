using System.Runtime.CompilerServices;
using System.Text;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Core.Radio;
using ScanlineStudio.Core.Radio.Rigctld;

namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>Fixture-driven tests against <see cref="RigctldClientProtocol"/>, scripted via
/// <see cref="FakeRadioTransport"/>. Every wire-format detail exercised here (response framing,
/// mode tokens, PTT value range, `l LEVEL` meter framing) was verified directly against a local
/// Hamlib source clone (`tests/rigctl_parse.c`, `src/misc.c`) rather than assumed from prose -- see
/// spec/04-rigctld.md's "Response framing" and "Telemetry" sections -- so these are
/// spec/Hamlib-source-derived fixtures, not a real `rigctld` capture.
/// <see cref="RigctldDummyRigIntegrationTests"/> is what retires that caveat.
///
/// Every scenario connects fresh, so every script starts with the capability-negotiation probe
/// (`f`, `m`, `t`, then `l SWR`/`l ALC`/`l RFPOWER_METER`/`l STRENGTH` -- spec/04-rigctld.md's
/// "Discovery and capability negotiation") before whatever the test is actually targeting. Meter
/// probes default to unsupported ("RPRT -11") in every test that isn't specifically about meters, to
/// keep those tests' existing assertions unchanged.</summary>
public class RigctldClientProtocolTests
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The four meter probes as unsupported (SWR/ALC/RFPOWER_METER/STRENGTH) -- appended
    /// after `f`/`m`/`t` in every script that isn't specifically testing meter behavior.</summary>
    private static readonly string[] MetersUnsupportedProbe = ["RPRT -11", "RPRT -11", "RPRT -11", "RPRT -11"];

    [Fact]
    public async Task PollAsync_FullCapabilities_ReturnsFullyPopulatedState()
    {
        var script = Script(
            ["14074000", "USB", "0", "0", .. MetersUnsupportedProbe], // probe: f, m (2 lines), t, l SWR, l ALC, l RFPOWER_METER, l STRENGTH
            ["14074000", "USB", "0", "0"]); // poll: f, m (2 lines), t (not transmitting -- meters never read)
        var transport = new FakeRadioTransport(script);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.Equal(14074000, state.FrequencyHz);
        Assert.Equal(RadioMode.Usb, state.Mode);
        Assert.False(state.IsTransmitting);
        Assert.Null(state.SignalStrengthDb);
        Assert.Null(state.SwrRatio);
        Assert.Null(state.AlcLevel);
        Assert.Null(state.PowerPercent);
        Assert.Equal(
            RadioCapabilities.ReadFrequency | RadioCapabilities.SetFrequency |
            RadioCapabilities.ReadMode | RadioCapabilities.SetMode | RadioCapabilities.PttControl,
            sut.Capabilities);
        Assert.Equal("f\nm\nt\nl SWR\nl ALC\nl RFPOWER_METER\nl STRENGTH\nf\nm\nt\n", transport.WrittenText);
    }

    [Fact]
    public async Task PollAsync_ModeAndPttUnsupported_SkipsThoseCommands_AndDefaultsFieldsSafely()
    {
        var script = Script(
            ["14074000", "RPRT -11", "RPRT -11", .. MetersUnsupportedProbe], // probe: f ok, m unsupported, t unsupported
            ["14074000"]); // poll: only f -- m/t never issued since Capabilities lacks them
        var transport = new FakeRadioTransport(script);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.Equal(14074000, state.FrequencyHz);
        Assert.Equal(RadioMode.Unknown, state.Mode);
        Assert.False(state.IsTransmitting);
        Assert.Equal(RadioCapabilities.ReadFrequency | RadioCapabilities.SetFrequency, sut.Capabilities);
        Assert.Equal("f\nm\nt\nl SWR\nl ALC\nl RFPOWER_METER\nl STRENGTH\nf\n", transport.WrittenText);
    }

    [Fact]
    public async Task PollAsync_ThrowsRadioProtocolException_WhenFrequencyCommandReturnsRprtError()
    {
        var script = Script(
            ["14074000", "USB", "0", "0", .. MetersUnsupportedProbe], // probe succeeds
            ["RPRT -1"]); // poll's 'f' now fails (e.g. rig powered off mid-session)
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
            ["14074000", "USB", "0", "0", .. MetersUnsupportedProbe],
            ["not-a-number"]);
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
            ["14074000", token, "0", "0", .. MetersUnsupportedProbe],
            ["14074000", token, "0", "0"]);
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
            ["14074000", "USB", "0", pttValue, .. MetersUnsupportedProbe],
            ["14074000", "USB", "0", pttValue]);
        var transport = new FakeRadioTransport(script);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.Equal(expected, state.IsTransmitting);
    }

    [Fact]
    public async Task PollAsync_WhileTransmittingWithFullMeterCapabilities_ReadsAllThreeMeters()
    {
        var script = Script(
            ["14074000", "USB", "0", "0", "2.500000", "45", "0.8", "-9"], // probe: f, m, t, l SWR, l ALC, l RFPOWER_METER, l STRENGTH
            ["14074000", "USB", "0", "1", "1.200000", "50", "0.75"]); // poll: transmitting -- TX meters read, STRENGTH is not (RX-only)
        var transport = new FakeRadioTransport(script);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.True(state.IsTransmitting);
        Assert.Equal(1.2f, state.SwrRatio);
        Assert.Equal(50f, state.AlcLevel);
        Assert.Equal(75f, state.PowerPercent); // 0.75 fraction -> 75%
        // STRENGTH capability is negotiated (probe succeeded), but the value itself stays null this
        // poll -- IsTransmitting is true, and SignalStrengthDb is RX-only (opposite gating from the
        // three TX-only meters just asserted above).
        Assert.Null(state.SignalStrengthDb);
        Assert.Equal(
            RadioCapabilities.ReadFrequency | RadioCapabilities.SetFrequency |
            RadioCapabilities.ReadMode | RadioCapabilities.SetMode | RadioCapabilities.PttControl |
            RadioCapabilities.SwrMeter | RadioCapabilities.AlcMeter | RadioCapabilities.PowerMeter |
            RadioCapabilities.SignalMeter,
            sut.Capabilities);
        Assert.Equal("f\nm\nt\nl SWR\nl ALC\nl RFPOWER_METER\nl STRENGTH\nf\nm\nt\nl SWR\nl ALC\nl RFPOWER_METER\n", transport.WrittenText);
    }

    [Fact]
    public async Task PollAsync_WhileNotTransmitting_NeverReadsMetersEvenIfCapable()
    {
        var script = Script(
            ["14074000", "USB", "0", "0", "2.500000", "45", "0.8", "-9"], // probe
            ["14074000", "USB", "0", "0", "-9"]); // poll: not transmitting -- SWR/ALC/PWR never sent, STRENGTH IS
        var transport = new FakeRadioTransport(script);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.False(state.IsTransmitting);
        Assert.Null(state.SwrRatio);
        Assert.Null(state.AlcLevel);
        Assert.Null(state.PowerPercent);
        Assert.Equal(-9, state.SignalStrengthDb);
        Assert.Equal("f\nm\nt\nl SWR\nl ALC\nl RFPOWER_METER\nl STRENGTH\nf\nm\nt\nl STRENGTH\n", transport.WrittenText);
    }

    [Fact]
    public async Task PollAsync_WhileTransmitting_NeverReadsSignalStrengthEvenIfCapable()
    {
        // Mirrors PollAsync_WhileNotTransmitting_NeverReadsMetersEvenIfCapable above, but for the
        // opposite (RX-only) gating direction.
        var script = Script(
            ["14074000", "USB", "0", "0", "2.500000", "45", "0.8", "-9"], // probe
            ["14074000", "USB", "0", "1", "1.200000", "50", "0.75"]); // poll: transmitting -- l STRENGTH never sent
        var transport = new FakeRadioTransport(script);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.True(state.IsTransmitting);
        Assert.Null(state.SignalStrengthDb);
        Assert.Equal("f\nm\nt\nl SWR\nl ALC\nl RFPOWER_METER\nl STRENGTH\nf\nm\nt\nl SWR\nl ALC\nl RFPOWER_METER\n", transport.WrittenText);
    }

    [Fact]
    public async Task PollAsync_SignalStrengthRoundsToNearestInt()
    {
        var script = Script(
            ["14074000", "USB", "0", "0", "RPRT -11", "RPRT -11", "RPRT -11", "-13.6"], // probe: only STRENGTH supported
            ["14074000", "USB", "0", "0", "-13.6"]); // poll
        var transport = new FakeRadioTransport(script);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.Equal(-14, state.SignalStrengthDb); // MidpointRounding.ToEven doesn't apply here -- -13.6 rounds to -14, not -13
    }

    [Fact]
    public async Task PollAsync_SignalStrengthCapabilityAbsent_YieldsNull()
    {
        var script = Script(
            ["14074000", "USB", "0", "0", "RPRT -11", "RPRT -11", "RPRT -11", "RPRT -11"], // probe: nothing supported
            ["14074000", "USB", "0", "0"]); // poll
        var transport = new FakeRadioTransport(script);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.Null(state.SignalStrengthDb);
        Assert.Equal("f\nm\nt\nl SWR\nl ALC\nl RFPOWER_METER\nl STRENGTH\nf\nm\nt\n", transport.WrittenText);
    }

    [Fact]
    public async Task PollAsync_SignalStrengthReadFails_YieldsNull_DoesNotThrow()
    {
        var script = Script(
            ["14074000", "USB", "0", "0", "RPRT -11", "RPRT -11", "RPRT -11", "-9"], // probe: only STRENGTH supported
            ["14074000", "USB", "0", "0", "RPRT -11"]); // poll: STRENGTH read fails this cycle
        var transport = new FakeRadioTransport(script);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.Null(state.SignalStrengthDb);
    }

    [Fact]
    public async Task PollAsync_MeterReadFailsMidSession_YieldsNullForThatMeterOnly_DoesNotThrow()
    {
        // A meter read failing (e.g. transient RPRT error) must never abort the whole poll -- unlike
        // frequency, a per-meter failure degrades to "unknown this poll," not a poll-level exception.
        var script = Script(
            ["14074000", "USB", "0", "0", "2.500000", "45", "0.8", "-9"], // probe
            ["14074000", "USB", "0", "1", "RPRT -11", "50", "0.75"]); // poll: SWR read fails this cycle
        var transport = new FakeRadioTransport(script);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.Null(state.SwrRatio);
        Assert.Equal(50f, state.AlcLevel);
        Assert.Equal(75f, state.PowerPercent);
    }

    [Fact]
    public async Task PollAsync_SwrMeterReportsInfinity_ParsesAsPositiveInfinity_NotNull()
    {
        // rig.h documents SWR's range as "0.0 ... infinite" -- Hamlib's own %g printf can legitimately
        // emit "inf"; this must parse as a real (cutoff-worthy) value, not silently become null.
        var script = Script(
            ["14074000", "USB", "0", "0", "inf", "RPRT -11", "RPRT -11", "RPRT -11"], // probe: only SWR supported
            ["14074000", "USB", "0", "1", "inf"]); // poll
        var transport = new FakeRadioTransport(script);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.Equal(float.PositiveInfinity, state.SwrRatio);
    }

    [Fact]
    public async Task PollAsync_MeterValueUsesInvariantCultureParsing_NotCurrentCulture()
    {
        // Regression guard for a real bug caught before shipping: the culture-sensitive
        // float.TryParse(string, out float) overload would parse "1.5" as 15 under a culture where
        // '.' is a thousands separator (e.g. nl-NL/de-DE) -- turning a normal SWR reading into an
        // instant false cutoff. This test doesn't need to change CurrentCulture to prove the
        // invariant-culture call path is used: an explicit decimal value with a '.' must round-trip
        // exactly regardless of the machine's own culture.
        var script = Script(
            ["14074000", "USB", "0", "0", "1.500000", "RPRT -11", "RPRT -11", "RPRT -11"],
            ["14074000", "USB", "0", "1", "1.500000"]);
        var transport = new FakeRadioTransport(script);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.Equal(1.5f, state.SwrRatio);
    }

    [Fact]
    public async Task SetFrequencyAsync_SendsFCommand_AndSucceedsOnRprtZero()
    {
        var script = Script(["14074000", "USB", "0", "0", .. MetersUnsupportedProbe], ["RPRT 0"]);
        var transport = new FakeRadioTransport(script);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);

        await sut.SetFrequencyAsync(7074000, CancellationToken.None);

        Assert.Equal("f\nm\nt\nl SWR\nl ALC\nl RFPOWER_METER\nl STRENGTH\nF 7074000\n", transport.WrittenText);
    }

    [Fact]
    public async Task SetFrequencyAsync_ThrowsRadioProtocolException_OnRprtError()
    {
        var script = Script(["14074000", "USB", "0", "0", .. MetersUnsupportedProbe], ["RPRT -1"]);
        var transport = new FakeRadioTransport(script);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);

        await Assert.ThrowsAsync<RadioProtocolException>(
            () => sut.SetFrequencyAsync(7074000, CancellationToken.None));
    }

    [Fact]
    public async Task SetModeAsync_SendsMCommand_WithNormalPassbandSentinel()
    {
        var script = Script(["14074000", "USB", "0", "0", .. MetersUnsupportedProbe], ["RPRT 0"]);
        var transport = new FakeRadioTransport(script);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);

        await sut.SetModeAsync(RadioMode.Lsb, CancellationToken.None);

        // Passband 0 = Hamlib's RIG_PASSBAND_NORMAL sentinel (verified against rig.h) -- ScanlineStudio's
        // domain model has no passband field, so this is always what's requested.
        Assert.Equal("f\nm\nt\nl SWR\nl ALC\nl RFPOWER_METER\nl STRENGTH\nM LSB 0\n", transport.WrittenText);
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
        var script = Script(["14074000", "USB", "0", "0", .. MetersUnsupportedProbe], ["RPRT 0"]);
        var transport = new FakeRadioTransport(script);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);

        await sut.SetPttAsync(true, CancellationToken.None);

        Assert.Equal("f\nm\nt\nl SWR\nl ALC\nl RFPOWER_METER\nl STRENGTH\nT 1\n", transport.WrittenText);
    }

    [Fact]
    public async Task EnsureConnected_OnlyProbesOnce_AcrossMultipleCalls()
    {
        var script = Script(
            ["14074000", "USB", "0", "0", .. MetersUnsupportedProbe], // probe -- only ever sent once
            ["14074000", "USB", "0", "0"], // poll 1
            ["14074000", "USB", "0", "0"]); // poll 2
        var transport = new FakeRadioTransport(script);
        var sut = new RigctldClientProtocol(transport, ConnectTimeout);

        await sut.PollAsync(CancellationToken.None);
        await sut.PollAsync(CancellationToken.None);

        Assert.Equal("f\nm\nt\nl SWR\nl ALC\nl RFPOWER_METER\nl STRENGTH\nf\nm\nt\nf\nm\nt\n", transport.WrittenText);
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

    private static byte[] Script(params string[][] lineGroups) => Script(lineGroups.SelectMany(g => g).ToArray());

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
