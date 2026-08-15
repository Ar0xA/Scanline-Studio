using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Core.Logbook.Tests;

public sealed class AdifUdpStreamerTests
{
    [Fact]
    public void BuildLoggedAdifDatagram_ProducesTheExactWsjtxWireFormat()
    {
        var datagram = AdifUdpStreamer.BuildLoggedAdifDatagram("Scanline Studio", "<call:6>N0CALL<eor>");

        // quint32 magic, big-endian, per WSJT-X's NetworkMessage.hpp: "static quint32 constexpr
        // magic {0xadbccbda}; // never change this"
        Assert.Equal(0xadbccbdaU, BinaryPrimitives.ReadUInt32BigEndian(datagram.AsSpan(0, 4)));
        // quint32 schema -- 2, the pre-negotiation-field baseline every receiver accepts
        Assert.Equal(2U, BinaryPrimitives.ReadUInt32BigEndian(datagram.AsSpan(4, 4)));
        // quint32 type -- 12 (LoggedADIF)
        Assert.Equal(12U, BinaryPrimitives.ReadUInt32BigEndian(datagram.AsSpan(8, 4)));

        // utf8 Id: quint32 big-endian byte-length prefix + raw UTF-8 bytes (Qt QByteArray framing)
        var idBytes = Encoding.UTF8.GetBytes("Scanline Studio");
        var offset = 12;
        Assert.Equal((uint)idBytes.Length, BinaryPrimitives.ReadUInt32BigEndian(datagram.AsSpan(offset, 4)));
        offset += 4;
        Assert.Equal(idBytes, datagram[offset..(offset + idBytes.Length)]);
        offset += idBytes.Length;

        // utf8 ADIF text: same framing
        var adifBytes = Encoding.UTF8.GetBytes("<call:6>N0CALL<eor>");
        Assert.Equal((uint)adifBytes.Length, BinaryPrimitives.ReadUInt32BigEndian(datagram.AsSpan(offset, 4)));
        offset += 4;
        Assert.Equal(adifBytes, datagram[offset..(offset + adifBytes.Length)]);
        offset += adifBytes.Length;

        Assert.Equal(offset, datagram.Length); // no trailing/extra bytes
    }

    [Fact]
    public void BuildLoggedAdifDatagram_NonAsciiAdifText_UsesUtf8ByteLengthNotCharCount()
    {
        // "café" is 4 chars but 5 UTF-8 bytes -- the length prefix must be the byte count (Qt
        // QByteArray framing), not String.Length, or the receiver reads a truncated/garbled value.
        // Escaped rather than a literal accented character so the assertion doesn't depend on this
        // .cs file being read back as UTF-8.
        const string nonAscii = "café";
        var datagram = AdifUdpStreamer.BuildLoggedAdifDatagram("id", nonAscii);

        var idLength = BinaryPrimitives.ReadUInt32BigEndian(datagram.AsSpan(12, 4));
        var adifLengthOffset = 12 + 4 + (int)idLength;
        var adifLength = BinaryPrimitives.ReadUInt32BigEndian(datagram.AsSpan(adifLengthOffset, 4));

        Assert.Equal(5U, adifLength);
        Assert.Equal(4, nonAscii.Length);
    }

    [Fact]
    public void BuildLoggedAdifDatagram_NonAsciiClientId_UsesUtf8ByteLengthNotCharCount()
    {
        // Same property as the adifText case above, but for the Id field specifically -- a
        // separate code path (WriteUtf8Field is called twice, once per field) worth pinning on
        // its own rather than assuming symmetry.
        const string nonAscii = "café";
        var datagram = AdifUdpStreamer.BuildLoggedAdifDatagram(nonAscii, "x");

        var idLength = BinaryPrimitives.ReadUInt32BigEndian(datagram.AsSpan(12, 4));

        Assert.Equal(5U, idLength);
        Assert.Equal(4, nonAscii.Length);
    }

    [Fact]
    public void BuildLoggedAdifDatagram_EmptyAdifText_WritesAZeroLengthPrefixNotANullSentinel()
    {
        var datagram = AdifUdpStreamer.BuildLoggedAdifDatagram("id", string.Empty);

        var idLength = BinaryPrimitives.ReadUInt32BigEndian(datagram.AsSpan(12, 4));
        var adifLengthOffset = 12 + 4 + (int)idLength;
        var adifLength = BinaryPrimitives.ReadUInt32BigEndian(datagram.AsSpan(adifLengthOffset, 4));

        Assert.Equal(0U, adifLength);
        Assert.Equal(adifLengthOffset + 4, datagram.Length); // no trailing bytes for a 0-length value
    }

    [Fact]
    public async Task SendLoggedQsoAsync_NoDestinations_ReturnsZeroZeroAndSendsNothing()
    {
        var settingsStore = new FakeSettingsStore();
        var streamer = new AdifUdpStreamer(settingsStore, NullLogger<AdifUdpStreamer>.Instance);

        var result = await streamer.SendLoggedQsoAsync("<call:6>N0CALL<eor>");

        Assert.Equal(0, result.SentCount);
        Assert.Equal(0, result.EnabledCount);
    }

    [Fact]
    public async Task SendLoggedQsoAsync_OneDisabledOneEnabled_OnlySendsToTheEnabledOne()
    {
        using var enabledListener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var enabledPort = ((IPEndPoint)enabledListener.Client.LocalEndPoint!).Port;
        using var disabledListener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var disabledPort = ((IPEndPoint)disabledListener.Client.LocalEndPoint!).Port;

        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AdifUdpStreamingSettings.SectionKey,
                new AdifUdpStreamingSettings
                {
                    Destinations =
                    [
                        new AdifUdpDestination { Enabled = true, Name = "Enabled", Host = "127.0.0.1", Port = enabledPort },
                        new AdifUdpDestination { Enabled = false, Name = "Disabled", Host = "127.0.0.1", Port = disabledPort },
                    ],
                    ClientId = "Scanline Studio",
                },
                AdifUdpStreamingSettingsJsonContext.Default.AdifUdpStreamingSettings),
        };
        var streamer = new AdifUdpStreamer(settingsStore, NullLogger<AdifUdpStreamer>.Instance);

        var receiveTask = enabledListener.ReceiveAsync();
        var result = await streamer.SendLoggedQsoAsync("<call:6>N0CALL<eor>");

        Assert.Equal(1, result.SentCount);
        Assert.Equal(1, result.EnabledCount);

        var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(receiveTask, completed);

        var disabledReceiveTask = disabledListener.ReceiveAsync();
        var disabledCompleted = await Task.WhenAny(disabledReceiveTask, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(disabledReceiveTask, disabledCompleted); // the disabled destination got nothing
    }

    [Fact]
    public async Task SendLoggedQsoAsync_MultipleEnabledDestinations_DeliversTheSameDatagramToEach()
    {
        using var listenerA = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var portA = ((IPEndPoint)listenerA.Client.LocalEndPoint!).Port;
        using var listenerB = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var portB = ((IPEndPoint)listenerB.Client.LocalEndPoint!).Port;

        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AdifUdpStreamingSettings.SectionKey,
                new AdifUdpStreamingSettings
                {
                    Destinations =
                    [
                        new AdifUdpDestination { Enabled = true, Name = "A", Host = "127.0.0.1", Port = portA },
                        new AdifUdpDestination { Enabled = true, Name = "B", Host = "127.0.0.1", Port = portB },
                    ],
                    ClientId = "Scanline Studio",
                },
                AdifUdpStreamingSettingsJsonContext.Default.AdifUdpStreamingSettings),
        };
        var streamer = new AdifUdpStreamer(settingsStore, NullLogger<AdifUdpStreamer>.Instance);

        var receiveA = listenerA.ReceiveAsync();
        var receiveB = listenerB.ReceiveAsync();
        var result = await streamer.SendLoggedQsoAsync("<call:6>N0CALL<eor>");

        Assert.Equal(2, result.SentCount);
        Assert.Equal(2, result.EnabledCount);

        var expected = AdifUdpStreamer.BuildLoggedAdifDatagram("Scanline Studio", "<call:6>N0CALL<eor>");
        await Task.WhenAll(receiveA, receiveB).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expected, (await receiveA).Buffer);
        Assert.Equal(expected, (await receiveB).Buffer);
    }

    /// <summary>One destination that can never resolve (a `.invalid`-TLD hostname NXDOMAINs fast,
    /// per RFC 2606 -- deliberately not a real timeout scenario, see this class's own plan's
    /// testing note on why a real slow-DNS scenario isn't practical to simulate deterministically
    /// here) must not prevent a second, real, reachable destination from still succeeding --
    /// pins the per-destination isolation `Task.WhenAll` is meant to provide.</summary>
    [Fact]
    public async Task SendLoggedQsoAsync_OneDestinationCannotResolve_TheOtherStillSucceeds()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var listenerPort = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AdifUdpStreamingSettings.SectionKey,
                new AdifUdpStreamingSettings
                {
                    Destinations =
                    [
                        new AdifUdpDestination { Enabled = true, Name = "Unreachable", Host = "this-host-does-not-exist.invalid", Port = 12345 },
                        new AdifUdpDestination { Enabled = true, Name = "Reachable", Host = "127.0.0.1", Port = listenerPort },
                    ],
                    ClientId = "Scanline Studio",
                },
                AdifUdpStreamingSettingsJsonContext.Default.AdifUdpStreamingSettings),
        };
        var streamer = new AdifUdpStreamer(settingsStore, NullLogger<AdifUdpStreamer>.Instance);

        var receiveTask = listener.ReceiveAsync();
        var result = await streamer.SendLoggedQsoAsync("<call:6>N0CALL<eor>");

        Assert.Equal(1, result.SentCount);
        Assert.Equal(2, result.EnabledCount);

        var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(receiveTask, completed);
    }

    /// <summary>Auditor code-review finding: a bare parameterless <see cref="UdpClient"/> is
    /// IPv4-only, not dual-mode, so an IPv6 destination -- an IPv6 literal here, but "localhost"
    /// itself resolves IPv6-first on Windows and most glibc Linux setups, making this the single
    /// most likely real "same-machine external logger" configuration, not a corner case -- would
    /// throw on send and get silently swallowed as a generic failure. Uses an IP LITERAL
    /// (<c>::1</c>), not <c>"localhost"</c>, so this test can't pass vacuously on a CI box that
    /// happens to resolve <c>localhost</c> to <c>127.0.0.1</c> first (per the plan's own testing
    /// note on why DNS-timing-dependent tests are unreliable here). Guarded on
    /// <see cref="Socket.OSSupportsIPv6"/> since this sandbox/CI environment may not have IPv6
    /// enabled at all.</summary>
    [Fact]
    public async Task SendLoggedQsoAsync_IPv6LiteralDestination_StillDeliversTheDatagram()
    {
        if (!Socket.OSSupportsIPv6)
        {
            return;
        }

        using var listener = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        var listenerPort = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AdifUdpStreamingSettings.SectionKey,
                new AdifUdpStreamingSettings
                {
                    Destinations = [new AdifUdpDestination { Enabled = true, Name = "IPv6", Host = "::1", Port = listenerPort }],
                    ClientId = "Scanline Studio",
                },
                AdifUdpStreamingSettingsJsonContext.Default.AdifUdpStreamingSettings),
        };
        var streamer = new AdifUdpStreamer(settingsStore, NullLogger<AdifUdpStreamer>.Instance);

        var receiveTask = listener.ReceiveAsync();
        var result = await streamer.SendLoggedQsoAsync("<call:6>N0CALL<eor>");

        Assert.Equal(1, result.SentCount);
        Assert.Equal(1, result.EnabledCount);

        var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(receiveTask, completed);
    }

    /// <summary>Auditor code-review finding: an enabled-but-malformed destination (empty Host here)
    /// must count toward <see cref="Abstractions.Logbook.AdifUdpSendResult.EnabledCount"/> even
    /// though it can never succeed -- an earlier version filtered it out before computing
    /// EnabledCount, which silently hid a typo'd destination behind a falsely "complete" N/N count
    /// (a working destination alone would have read as "sent to 1/1" instead of the true "1/2, one
    /// destination is broken").</summary>
    [Fact]
    public async Task SendLoggedQsoAsync_OneEnabledDestinationHasNoHost_StillCountsTowardEnabledButNeverSent()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var listenerPort = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AdifUdpStreamingSettings.SectionKey,
                new AdifUdpStreamingSettings
                {
                    Destinations =
                    [
                        new AdifUdpDestination { Enabled = true, Name = "Broken", Host = null, Port = 9999 },
                        new AdifUdpDestination { Enabled = true, Name = "Working", Host = "127.0.0.1", Port = listenerPort },
                    ],
                    ClientId = "Scanline Studio",
                },
                AdifUdpStreamingSettingsJsonContext.Default.AdifUdpStreamingSettings),
        };
        var streamer = new AdifUdpStreamer(settingsStore, NullLogger<AdifUdpStreamer>.Instance);

        var receiveTask = listener.ReceiveAsync();
        var result = await streamer.SendLoggedQsoAsync("<call:6>N0CALL<eor>");

        Assert.Equal(1, result.SentCount);
        Assert.Equal(2, result.EnabledCount); // the broken one still counts as "enabled"

        var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(receiveTask, completed);
    }
}
