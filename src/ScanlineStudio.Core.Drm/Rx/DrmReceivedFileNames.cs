using System.Text;

namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>Received-file name hardening (interpretation side, plan "RX payload handling"). QSSTV uses the
/// transmitted name raw as a path below its RX folder (sourcedecoder.cpp:664,696; reedsolomoncoder.cpp:163-166), so a
/// name with "/" or ".." writes elsewhere and an existing file is overwritten. These helpers keep only a safe file name;
/// the consumer's file store must still never overwrite (<see cref="MakeUnique"/>).</summary>
public static class DrmReceivedFileNames
{
    /// <summary>Longest name returned, in UTF-16 code units.</summary>
    public const int MaxLength = 120;

    /// <summary>Longest name returned, in UTF-8 bytes (ext4 and most file systems allow 255).</summary>
    public const int MaxUtf8Bytes = 240;

    /// <summary>Name used when nothing usable is left.</summary>
    public const string Fallback = "drm-file";

    private const int MaxExtensionLength = 16;

    /// <summary>Makes <paramref name="name"/> a safe single file name on Windows, macOS and Linux: only the text after
    /// the last '/' or '\'; control characters (C0, DEL, C1), Unicode format characters (Cf, e.g. U+202E) and <c>&lt;&gt;:"/\|?*</c> become '_'; leading dots
    /// become '_' (no hidden files); trailing dots and spaces are removed (Windows drops them); a
    /// reserved Windows device name (CON, PRN, AUX, NUL, COM0-9, LPT0-9, also with a suffix) gets a '_' prefix; the
    /// stem is shortened to <see cref="MaxLength"/> / <see cref="MaxUtf8Bytes"/>, keeping the extension;
    /// an empty result becomes <see cref="Fallback"/>.</summary>
    public static string Sanitize(string? name)
    {
        string s = name ?? string.Empty;
        int cut = Math.Max(s.LastIndexOf('/'), s.LastIndexOf('\\'));
        if (cut >= 0)
            s = s[(cut + 1)..];
        if (s.Trim('.', ' ').Length == 0)
            return Fallback;

        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(IsInvalid(c) ? '_' : c);
        for (int i = 0; i < sb.Length && sb[i] == '.'; i++)
            sb[i] = '_';
        s = sb.ToString().TrimEnd('.', ' ').TrimStart(' ');
        if (s.Length == 0)
            return Fallback;

        if (IsReservedDeviceName(s))
            s = "_" + s;
        return Shorten(s);
    }

    /// <summary>Returns <paramref name="safeName"/>, or "stem (n).ext" with the smallest n ≥ 1 for which
    /// <paramref name="exists"/> is false, so a received file never overwrites another.</summary>
    public static string MakeUnique(string safeName, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(safeName);
        ArgumentNullException.ThrowIfNull(exists);
        if (!exists(safeName))
            return safeName;

        (string stem, string ext) = Split(safeName);
        for (int n = 1; n < int.MaxValue; n++)
        {
            string suffix = $" ({n})";
            string candidate = Shorten(stem, ext, suffix);
            if (!exists(candidate))
                return candidate;
        }

        throw new IOException("No free file name.");
    }

    private static bool IsInvalid(char c) =>
        char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format || c < 0x20 || c is >= '\u007f' and <= '\u009f' || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*';

    private static bool IsReservedDeviceName(string s)
    {
        int dot = s.IndexOf('.');
        string stem = (dot < 0 ? s : s[..dot]).TrimEnd(' ');
        if (stem.Length == 3)
            return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase);
        if (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
            return stem[3] is (>= '0' and <= '9') or '¹' or '²' or '³';
        return false;
    }

    private static (string Stem, string Ext) Split(string s)
    {
        int dot = s.LastIndexOf('.');
        return dot > 0 && s.Length - dot <= MaxExtensionLength ? (s[..dot], s[dot..]) : (s, string.Empty);
    }

    private static string Shorten(string s)
    {
        (string stem, string ext) = Split(s);
        return Shorten(stem, ext, string.Empty);
    }

    private static string Shorten(string stem, string ext, string suffix)
    {
        string tail = suffix + ext;
        while (stem.Length > 0 && (stem.Length + tail.Length > MaxLength || Encoding.UTF8.GetByteCount(stem + tail) > MaxUtf8Bytes))
        {
            int drop = char.IsLowSurrogate(stem[^1]) && stem.Length > 1 ? 2 : 1;
            stem = stem[..^drop];
        }

        stem = stem.TrimEnd('.', ' ');
        if (stem.Length == 0)
            stem = Fallback;
        return stem + tail;
    }
}
