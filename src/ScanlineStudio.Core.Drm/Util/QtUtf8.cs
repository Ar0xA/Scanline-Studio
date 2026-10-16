using System.Text;

namespace ScanlineStudio.Core.Drm.Util;

/// <summary>Qt 5.15's UTF-8 decoder as reached through <c>QString = QByteArray</c>
/// (qstring.h:840-841 → <c>fromUtf8</c>, which stops at the first NUL, qstring.h:711-712).
/// QSSTV runs every MOT name through it (MOTSlideShow.cpp:130 assigns <c>name.toLatin1()</c> to a
/// QString), so it shapes the bytes sent on air.</summary>
internal static class QtUtf8
{
    private const char ReplacementCharacter = '�';

    /// <summary>Decodes up to the first NUL. A leading BOM is skipped. Every byte that does not start a
    /// valid sequence becomes U+FFFD and decoding resumes at the next byte, so an invalid
    /// two-byte sequence gives two replacement characters. Noncharacters are accepted
    /// (QUtf8::convertToUnicode, QUtf8Functions::fromUtf8 with QUtf8BaseTraits, qutfcodec*.cpp/h).</summary>
    public static string DecodeNulTerminated(ReadOnlySpan<byte> bytes)
    {
        int nul = bytes.IndexOf((byte)0);
        if (nul >= 0)
            bytes = bytes[..nul];

        var sb = new StringBuilder(bytes.Length);
        int pos = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            pos = 3;

        while (pos < bytes.Length)
        {
            byte b = bytes[pos++];
            if (b < 0x80)
            {
                sb.Append((char)b);
                continue;
            }

            int consumed = TryDecodeMultiByte(b, bytes[pos..], out int codePoint);
            if (consumed < 0)
            {
                sb.Append(ReplacementCharacter);
                continue;
            }

            pos += consumed;
            if (codePoint >= 0x10000)
                sb.Append(char.ConvertFromUtf32(codePoint));
            else
                sb.Append((char)codePoint);
        }

        return sb.ToString();
    }

    // Returns the number of continuation bytes consumed, or -1 when Qt reports Error/EndOfString.
    private static int TryDecodeMultiByte(byte lead, ReadOnlySpan<byte> rest, out int codePoint)
    {
        codePoint = 0;
        int charsNeeded;
        int minCodePoint;
        int uc;
        if (lead <= 0xC1)
            return -1;
        if (lead < 0xE0)
        {
            charsNeeded = 2;
            minCodePoint = 0x80;
            uc = lead & 0x1F;
        }
        else if (lead < 0xF0)
        {
            charsNeeded = 3;
            minCodePoint = 0x800;
            uc = lead & 0x0F;
        }
        else if (lead < 0xF5)
        {
            charsNeeded = 4;
            minCodePoint = 0x10000;
            uc = lead & 0x07;
        }
        else
        {
            return -1;
        }

        if (rest.Length < charsNeeded - 1)
            return -1;

        for (int k = 0; k < charsNeeded - 1; k++)
        {
            byte c = rest[k];
            if ((c & 0xC0) != 0x80)
                return -1;
            uc = (uc << 6) | (c & 0x3F);
        }

        if (uc < minCodePoint || uc is >= 0xD800 and <= 0xDFFF || uc > 0x10FFFF)
            return -1;

        codePoint = uc;
        return charsNeeded - 1;
    }
}
