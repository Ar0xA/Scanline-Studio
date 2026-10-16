using ScanlineStudio.Core.Drm.Util;

namespace ScanlineStudio.Core.Drm.Tests;

/// <summary>Qt toLatin1/fromLatin1 behaviour for on-air DRM text.</summary>
public class DrmLatin1Tests
{
    [Fact]
    public void Ascii_RoundTrips()
    {
        byte[] b = DrmLatin1.Encode("PA0TST image_01.jp2");
        Assert.Equal("PA0TST image_01.jp2"u8.ToArray(), b);
        Assert.Equal("PA0TST image_01.jp2", DrmLatin1.Decode(b));
    }

    [Fact]
    public void LatinCharacter_IsOneByte()
    {
        Assert.Equal(new byte[] { 0x63, 0x61, 0x66, 0xE9 }, DrmLatin1.Encode("café"));
        Assert.Equal("café", DrmLatin1.Decode([0x63, 0x61, 0x66, 0xE9]));
    }

    [Fact]
    public void EuroSign_BecomesQuestionMark()
    {
        Assert.Equal("5?"u8.ToArray(), DrmLatin1.Encode("5€"));
    }

    [Fact]
    public void Emoji_BecomesTwoQuestionMarks()
    {
        // U+1F600 is a surrogate pair; Qt converts each UTF-16 unit separately.
        Assert.Equal("a??b"u8.ToArray(), DrmLatin1.Encode("a\U0001F600b"));
    }

    [Fact]
    public void Decode_MapsEveryByteToSameCodePoint()
    {
        byte[] all = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        string s = DrmLatin1.Decode(all);
        Assert.Equal(256, s.Length);
        for (int i = 0; i < 256; i++)
            Assert.Equal((char)i, s[i]);
        Assert.Equal(all, DrmLatin1.Encode(s));
    }
}
