using System.Globalization;

namespace ScanlineStudio.Application;

/// <summary>
/// A <c>MAJOR.MINOR.PATCH[-prerelease][+build]</c> version with SemVer 2.0 precedence: numeric core first,
/// a prerelease ranks below its release, prerelease identifiers compare left to right (numeric below
/// alphanumeric, numeric by value, alphanumeric by ordinal ASCII), and fewer identifiers rank lower when
/// all shared ones are equal. A leading <c>v</c> (git tag style) is accepted; build metadata is ignored.
/// </summary>
public sealed class AppVersion : IComparable<AppVersion>, IEquatable<AppVersion>
{
    private readonly string[] _prerelease;

    private AppVersion(int major, int minor, int patch, string[] prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _prerelease = prerelease;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public bool IsPrerelease => _prerelease.Length > 0;

    /// <summary>Returns null for anything that is not a valid version (including numbers too large for <c>int</c>).</summary>
    public static AppVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }

        var plus = s.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            s = s[..plus];
        }

        var dash = s.IndexOf('-', StringComparison.Ordinal);
        var core = dash >= 0 ? s[..dash] : s;
        var prerelease = dash >= 0 ? s[(dash + 1)..].Split('.') : [];

        var parts = core.Split('.');
        if (parts.Length != 3
            || !TryParseNumber(parts[0], out var major)
            || !TryParseNumber(parts[1], out var minor)
            || !TryParseNumber(parts[2], out var patch))
        {
            return null;
        }

        if (dash >= 0 && prerelease.Any(id => id.Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
        {
            return null;
        }

        return new AppVersion(major, minor, patch, prerelease);
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = Major != other.Major ? Major.CompareTo(other.Major)
            : Minor != other.Minor ? Minor.CompareTo(other.Minor)
            : Patch.CompareTo(other.Patch);
        if (core != 0)
        {
            return core;
        }

        if (!IsPrerelease || !other.IsPrerelease)
        {
            return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;
        }

        for (var i = 0; i < Math.Min(_prerelease.Length, other._prerelease.Length); i++)
        {
            var result = CompareIdentifier(_prerelease[i], other._prerelease[i]);
            if (result != 0)
            {
                return result;
            }
        }

        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    public bool Equals(AppVersion? other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is AppVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', _prerelease.Select(id => id.All(char.IsAsciiDigit) ? id.TrimStart('0') : id)));

    public static bool operator ==(AppVersion? left, AppVersion? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(AppVersion? left, AppVersion? right) => !(left == right);

    public static bool operator <(AppVersion? left, AppVersion? right) => left is null ? right is not null : left.CompareTo(right) < 0;

    public static bool operator <=(AppVersion? left, AppVersion? right) => left is null || left.CompareTo(right) <= 0;

    public static bool operator >(AppVersion? left, AppVersion? right) => left is not null && left.CompareTo(right) > 0;

    public static bool operator >=(AppVersion? left, AppVersion? right) => left is null ? right is null : left.CompareTo(right) >= 0;

    public override string ToString() =>
        IsPrerelease ? $"{Major}.{Minor}.{Patch}-{string.Join('.', _prerelease)}" : $"{Major}.{Minor}.{Patch}";

    private static bool TryParseNumber(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static int CompareIdentifier(string a, string b)
    {
        var aNumeric = a.All(char.IsAsciiDigit);
        var bNumeric = b.All(char.IsAsciiDigit);
        if (aNumeric && bNumeric)
        {
            // Compare by length first so arbitrarily long numeric identifiers never overflow.
            var trimmedA = a.TrimStart('0');
            var trimmedB = b.TrimStart('0');
            return trimmedA.Length != trimmedB.Length
                ? trimmedA.Length.CompareTo(trimmedB.Length)
                : string.CompareOrdinal(trimmedA, trimmedB);
        }

        if (aNumeric != bNumeric)
        {
            return aNumeric ? -1 : 1;
        }

        return Math.Sign(string.CompareOrdinal(a, b));
    }
}
