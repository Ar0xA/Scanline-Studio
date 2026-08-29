using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Core.Radio.OmniRig;

namespace ScanlineStudio.Core.Radio.Tests;

public sealed class OmniRigRadioProtocolTests
{
    private static OmniRigRadioProtocol CreateSut(FakeOmniRigComClient client) =>
        new(client, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

    [Fact]
    public void RigId_IsOmniRigClient() =>
        Assert.Equal("omnirig-client", CreateSut(new FakeOmniRigComClient()).RigId);

    // Round-2 code-review finding: pins that EnsureConnectedAsync runs under its own connectTimeout
    // budget, not nested inside the shorter per-call requestTimeout -- a connect delay longer than
    // requestTimeout but within connectTimeout must still succeed. A regression that re-nests
    // EnsureConnectedAsync inside WithTransactionTimeoutAsync would fail this with a TimeoutException.
    [Fact]
    public async Task PollAsync_ConnectSlowerThanRequestTimeoutButWithinConnectTimeout_Succeeds()
    {
        var client = new FakeOmniRigComClient { ConnectDelay = TimeSpan.FromMilliseconds(300) };
        var sut = new OmniRigRadioProtocol(client, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(5));

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.Equal(14_070_000, state.FrequencyHz);
    }

    [Fact]
    public async Task PollAsync_ConnectsOnFirstCallOnly()
    {
        var client = new FakeOmniRigComClient();
        var sut = CreateSut(client);

        await sut.PollAsync(CancellationToken.None);
        await sut.PollAsync(CancellationToken.None);

        Assert.Equal(1, client.ConnectCallCount);
    }

    [Fact]
    public async Task PollAsync_Online_ReturnsFrequencyModeAndPtt()
    {
        var client = new FakeOmniRigComClient
        {
            Status = RigStatusX.Online,
            FrequencyHz = 14_070_000,
            Mode = RigParamX.PM_SSB_U,
            Tx = RigParamX.PM_TX,
        };
        var sut = CreateSut(client);

        var state = await sut.PollAsync(CancellationToken.None);

        Assert.Equal(14_070_000, state.FrequencyHz);
        Assert.Equal(RadioMode.Usb, state.Mode);
        Assert.True(state.IsTransmitting);
    }

    [Fact]
    public async Task PollAsync_Online_SetsCapabilitiesOnce()
    {
        var sut = CreateSut(new FakeOmniRigComClient { Status = RigStatusX.Online });

        Assert.Equal(RadioCapabilities.None, sut.Capabilities);
        await sut.PollAsync(CancellationToken.None);

        var expected = RadioCapabilities.ReadFrequency | RadioCapabilities.SetFrequency |
                       RadioCapabilities.ReadMode | RadioCapabilities.SetMode | RadioCapabilities.PttControl;
        Assert.Equal(expected, sut.Capabilities);
    }

    [Theory]
    [InlineData(RigStatusX.NotConfigured)]
    [InlineData(RigStatusX.Disabled)]
    [InlineData(RigStatusX.PortBusy)]
    [InlineData(RigStatusX.NotResponding)]
    public async Task PollAsync_NotOnline_ThrowsWithStatusText(RigStatusX status)
    {
        var client = new FakeOmniRigComClient { Status = status, StatusText = "some status text" };
        var sut = CreateSut(client);

        var ex = await Assert.ThrowsAsync<RadioProtocolException>(() => sut.PollAsync(CancellationToken.None));
        Assert.Contains("some status text", ex.Message, StringComparison.Ordinal);
    }

    // The real COMException -> RadioProtocolException translation lives in OmniRigComClient.ConnectAsync
    // (untestable here without a real COM install) -- this only verifies EnsureConnectedAsync
    // propagates whatever IOmniRigComClient.ConnectAsync throws, unwrapped.
    [Fact]
    public async Task PollAsync_ConnectFails_PropagatesConnectException()
    {
        var client = new FakeOmniRigComClient { ConnectException = new InvalidOperationException("boom") };
        var sut = CreateSut(client);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.PollAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SetFrequencyAsync_WithinRange_SetsFrequency()
    {
        var client = new FakeOmniRigComClient();
        var sut = CreateSut(client);

        await sut.SetFrequencyAsync(7_040_000, CancellationToken.None);

        Assert.Equal(7_040_000, client.FrequencyHz);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData((long)int.MaxValue + 1)]
    public async Task SetFrequencyAsync_OutOfInt32Range_Throws(long hz)
    {
        var sut = CreateSut(new FakeOmniRigComClient());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sut.SetFrequencyAsync(hz, CancellationToken.None));
    }

    [Fact]
    public async Task SetModeAsync_KnownMode_SetsRigParamX()
    {
        var client = new FakeOmniRigComClient();
        var sut = CreateSut(client);

        await sut.SetModeAsync(RadioMode.Lsb, CancellationToken.None);

        Assert.Equal(RigParamX.PM_SSB_L, client.Mode);
    }

    [Fact]
    public async Task SetModeAsync_UnmappableMode_Throws()
    {
        var sut = CreateSut(new FakeOmniRigComClient());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sut.SetModeAsync(RadioMode.Unknown, CancellationToken.None));
    }

    [Theory]
    [InlineData(true, RigParamX.PM_TX)]
    [InlineData(false, RigParamX.PM_RX)]
    public async Task SetPttAsync_SetsExplicitTxOrRxFlag(bool tx, RigParamX expected)
    {
        var client = new FakeOmniRigComClient();
        var sut = CreateSut(client);

        await sut.SetPttAsync(tx, CancellationToken.None);

        Assert.Equal(expected, client.Tx);
    }

    [Fact]
    public async Task SetBandwidthAsync_Throws()
    {
        var sut = CreateSut(new FakeOmniRigComClient());

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.SetBandwidthAsync(2400, CancellationToken.None));
    }

    [Fact]
    public async Task DisposeAsync_DisposesUnderlyingClient()
    {
        var client = new FakeOmniRigComClient();
        var sut = CreateSut(client);

        await sut.DisposeAsync();

        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task DisposeAsync_ThenAnyCall_Throws()
    {
        var sut = CreateSut(new FakeOmniRigComClient());
        await sut.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => sut.PollAsync(CancellationToken.None));
    }
}
