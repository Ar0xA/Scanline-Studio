namespace ScanlineStudio.Core.Drm.Util;

/// <summary>Latin-1 conversion for on-air DRM text (MOT names, FAC label, BSR text). Matches Qt's
/// QString::toLatin1/fromLatin1 as QSSTV uses them (MOTSlideShow.cpp:100-105, sourcedecoder.cpp:395).</summary>
internal static class DrmLatin1
{
    /// <summary>One byte per UTF-16 code unit; units above 0xFF become '?', so a surrogate pair
    /// (e.g. an emoji) becomes "??", as Qt's toLatin1 does.</summary>
    public static byte[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bytes[i] = c > 0xFF ? (byte)'?' : (byte)c;
        }

        return bytes;
    }

    /// <summary>Byte to char, one to one (fromLatin1).</summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
            chars[i] = (char)bytes[i];
        return new string(chars);
    }
}
