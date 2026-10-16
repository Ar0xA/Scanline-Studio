namespace ScanlineStudio.Abstractions.Drm;

/// <summary>The callsign as DRM sends it in the FAC label, for display. The transmitter is always given the raw
/// callsign; this only shows what the receiver will see.</summary>
public static class DrmCallsign
{
    /// <summary>FAC label length (FAC.cpp:152-171).</summary>
    public const int MaxLength = 9;

    /// <summary>Latin-1 per UTF-16 code unit ('?' above 0xFF), cut at the first NUL (<c>toLatin1().data()</c>,
    /// drmtransmitter.cpp:112), at most 9 characters, each sent as 7 bits (<c>c &amp; 127</c>).</summary>
    public static string Normalize(string? callsign)
    {
        if (string.IsNullOrEmpty(callsign))
        {
            return string.Empty;
        }

        var chars = new char[Math.Min(callsign.Length, MaxLength)];
        var length = 0;
        foreach (var c in callsign)
        {
            var latin1 = c > 0xFF ? '?' : c;
            if (latin1 == '\0' || length == MaxLength)
            {
                break;
            }

            chars[length++] = (char)(latin1 & 127);
        }

        return new string(chars, 0, length);
    }
}
