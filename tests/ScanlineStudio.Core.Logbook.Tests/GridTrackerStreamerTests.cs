using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Core.Logbook.Tests;

public sealed class GridTrackerStreamerTests
{
    [Fact]
    public void BuildLoggedAdifDatagram_ProducesTheExactWsjtxWireFormat()
    {
        var datagram = GridTrackerStreamer.BuildLoggedAdifDatagram("Scanline Studio", "<call:6>N0CALL<eor>");

        // quint32 magic, big-endian, per WSJT-X's NetworkMessage.hpp: "static quint32 constexpr
        // magic {0xadbccbda}; // never change this"
        Assert.Equal(0xadbccbdaU, BinaryPrimitives.ReadUInt32BigEndian(datagram.AsSpan(0, 4)));
        // quint32 schema -- 2, the pre-negotiation-field baseline every GridTracker version accepts
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
        var datagram = GridTrackerStreamer.BuildLoggedAdifDatagram("id", nonAscii);

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
        var datagram = GridTrackerStreamer.BuildLoggedAdifDatagram(nonAscii, "x");

        var idLength = BinaryPrimitives.ReadUInt32BigEndian(datagram.AsSpan(12, 4));

        Assert.Equal(5U, idLength);
        Assert.Equal(4, nonAscii.Length);
    }

    [Fact]
    public void BuildLoggedAdifDatagram_EmptyAdifText_WritesAZeroLengthPrefixNotANullSentinel()
    {
        var datagram = GridTrackerStreamer.BuildLoggedAdifDatagram("id", string.Empty);

        var idLength = BinaryPrimitives.ReadUInt32BigEndian(datagram.AsSpan(12, 4));
        var adifLengthOffset = 12 + 4 + (int)idLength;
        var adifLength = BinaryPrimitives.ReadUInt32BigEndian(datagram.AsSpan(adifLengthOffset, 4));

        Assert.Equal(0U, adifLength);
        Assert.Equal(adifLengthOffset + 4, datagram.Length); // no trailing bytes for a 0-length value
    }

    [Fact]
    public async Task SendLoggedQsoAsync_Disabled_ReturnsFalseAndSendsNothing()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var listenerPort = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        var settingsStore = new FakeSettingsStore
        {
            // Enabled left null (disabled) but pointed at a real listener -- proves "disabled"
            // actually skips the send, not just that the return value happens to be false.
            Settings = new AppSettings().WithSection(
                GridTrackerStreamingSettings.SectionKey,
                new GridTrackerStreamingSettings { Host = "127.0.0.1", Port = listenerPort },
                GridTrackerStreamingSettingsJsonContext.Default.GridTrackerStreamingSettings),
        };
        var streamer = new GridTrackerStreamer(settingsStore, NullLogger<GridTrackerStreamer>.Instance);

        var sent = await streamer.SendLoggedQsoAsync("<call:6>N0CALL<eor>");
        Assert.False(sent);

        var receiveTask = listener.ReceiveAsync();
        var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(receiveTask, completed); // nothing arrived within the timeout
    }

    [Fact]
    public async Task SendLoggedQsoAsync_Enabled_ActuallyDeliversTheDatagramOverARealLoopbackSocket()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var listenerPort = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                GridTrackerStreamingSettings.SectionKey,
                new GridTrackerStreamingSettings { Enabled = true, Host = "127.0.0.1", Port = listenerPort, ClientId = "Scanline Studio" },
                GridTrackerStreamingSettingsJsonContext.Default.GridTrackerStreamingSettings),
        };
        var streamer = new GridTrackerStreamer(settingsStore, NullLogger<GridTrackerStreamer>.Instance);

        var receiveTask = listener.ReceiveAsync();
        var sendTask = streamer.SendLoggedQsoAsync("<call:6>N0CALL<eor>");

        var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(receiveTask, completed);

        var sent = await sendTask;
        Assert.True(sent);

        var received = (await receiveTask).Buffer;
        var expected = GridTrackerStreamer.BuildLoggedAdifDatagram("Scanline Studio", "<call:6>N0CALL<eor>");
        Assert.Equal(expected, received);
    }

    [Fact]
    public async Task ResolveAsync_NoSectionConfigured_DefaultsToDisabledWithStandardHostAndPort()
    {
        var settings = await GridTrackerStreamingSettings.ResolveAsync(new FakeSettingsStore());

        Assert.Null(settings.Enabled);
        Assert.Equal("127.0.0.1", GridTrackerStreamingSettings.DefaultHost);
        Assert.Equal(2237, GridTrackerStreamingSettings.DefaultPort);
    }
}
