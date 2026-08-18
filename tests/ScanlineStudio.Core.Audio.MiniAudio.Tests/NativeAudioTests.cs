using ScanlineStudio.Core.Audio.MiniAudio;

namespace ScanlineStudio.Core.Audio.MiniAudio.Tests;

/// <summary>
/// Round-1 functional-audit addition (Batch 1, native/managed audio boundary): <see
/// cref="NativeAudio.EncodeFixedString"/>/<see cref="NativeAudio.DecodeFixedString"/> are pure,
/// deterministic, boundary-critical functions (every device id/name crossing the P/Invoke
/// boundary goes through them) with zero direct hardware-free test coverage before this file --
/// only indirectly reachable via <c>MiniAudioEngineTests.StartCaptureAsync_WithOverLongDeviceId_ThrowsAudioDeviceUnavailableException</c>'s
/// throw-path exercise of <see cref="NativeAudio.EncodeFixedString"/> alone.
/// </summary>
public class NativeAudioTests
{
    [Fact]
    public void EncodeThenDecodeFixedString_RoundTripsExactValue()
    {
        var encoded = NativeAudio.EncodeFixedString("hw:0,0", size: 64);
        var decoded = NativeAudio.DecodeFixedString(encoded);

        Assert.Equal("hw:0,0", decoded);
    }

    [Fact]
    public void EncodeFixedString_ProducesBufferOfExactlyRequestedSize()
    {
        var encoded = NativeAudio.EncodeFixedString("short", size: 32);

        Assert.Equal(32, encoded.Length);
    }

    [Fact]
    public void EncodeFixedString_NullTerminatesAndZeroFillsRemainder()
    {
        var encoded = NativeAudio.EncodeFixedString("ab", size: 8);

        Assert.Equal((byte)'a', encoded[0]);
        Assert.Equal((byte)'b', encoded[1]);
        Assert.All(encoded[2..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void EncodeFixedString_EmptyString_ProducesAllZeroBuffer()
    {
        var encoded = NativeAudio.EncodeFixedString(string.Empty, size: 16);

        Assert.All(encoded, b => Assert.Equal(0, b));
    }

    [Fact]
    public void EncodeFixedString_ValueExactlyFillingBufferWithNoRoomForTerminator_Throws()
    {
        // "abcd" is 4 bytes -- a 4-byte buffer has no room left for the null terminator, which is
        // exactly the off-by-one boundary this method's own `>=` check exists to catch.
        Assert.Throws<ArgumentException>(() => NativeAudio.EncodeFixedString("abcd", size: 4));
    }

    [Fact]
    public void EncodeFixedString_ValueOneByteShortOfBuffer_Succeeds()
    {
        // "abc" is 3 bytes into a 4-byte buffer -- exactly 1 byte left for the terminator, the
        // boundary case immediately on the other side of the test above.
        var encoded = NativeAudio.EncodeFixedString("abc", size: 4);

        Assert.Equal(4, encoded.Length);
        Assert.Equal(0, encoded[3]);
    }

    [Fact]
    public void EncodeFixedString_MultiByteUtf8Value_CountsEncodedBytesNotCharacters()
    {
        // "café" is 4 characters but 5 UTF-8 bytes (é is 2 bytes) -- a length check against
        // value.Length instead of the encoded byte count would silently accept this into a 5-byte
        // buffer (no room for the terminator) rather than throwing.
        Assert.Throws<ArgumentException>(() => NativeAudio.EncodeFixedString("café", size: 5));

        var encoded = NativeAudio.EncodeFixedString("café", size: 6);
        Assert.Equal("café", NativeAudio.DecodeFixedString(encoded));
    }

    [Fact]
    public void DecodeFixedString_StopsAtFirstNullByte_IgnoringTrailingGarbageBytes()
    {
        var buffer = new byte[] { (byte)'h', (byte)'i', 0, 0xFF, 0xFE };

        var decoded = NativeAudio.DecodeFixedString(buffer);

        Assert.Equal("hi", decoded);
    }

    [Fact]
    public void DecodeFixedString_WithNoNullByte_DecodesEntireBuffer()
    {
        var buffer = new byte[] { (byte)'h', (byte)'i' };

        var decoded = NativeAudio.DecodeFixedString(buffer);

        Assert.Equal("hi", decoded);
    }

    [Fact]
    public void DecodeFixedString_AllZeroBuffer_DecodesToEmptyString()
    {
        var decoded = NativeAudio.DecodeFixedString(new byte[16]);

        Assert.Equal(string.Empty, decoded);
    }
}
