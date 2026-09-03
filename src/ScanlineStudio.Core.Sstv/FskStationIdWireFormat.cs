using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// Shared wire-format rules for the FSK station-ID packet (STX <c>0x2a</c>) that both
/// <see cref="FskStationIdEncoder"/> (TX) and the RX continuation decoder must agree on
/// byte-for-byte -- extracted specifically because round-1/round-2 auditor plan-review found this
/// (the NR/RST compact-vs-string predicate) genuinely easy to get wrong, unlike the trivial XOR/
/// offset math. See the implementation plan's Ground Truth section for the full legacy citation
/// trail (`Main.cpp:6903-6965` TX, `sstv.cpp:2465-2551` RX).
/// </summary>
internal static class FskStationIdWireFormat
{
    /// <summary>fsk_cwid.md B-P1 layering move: alias, not a second definition --
    /// <see cref="StationIdCallsignNormalizer.MaxCallsignLength"/> (now in
    /// <c>ScanlineStudio.Abstractions.Sstv</c>) is the single source of truth, carrying the real
    /// citation (`sstv.h:718-722` sizes `m_fskcall[20]` etc; legacy aborts RX at 17 chars,
    /// `sstv.cpp:2478`, so 16 is the real maximum). Kept here too since callers throughout this file
    /// already reference `FskStationIdWireFormat.MaxCallsignLength` directly.</summary>
    public const int MaxCallsignLength = StationIdCallsignNormalizer.MaxCallsignLength;

    /// <summary>Legacy aborts RX at 9 chars (`sstv.cpp:2518`), so 8 is the real maximum for the
    /// NR/RST STRING sub-form specifically (the compact 2-byte form has no length concept of its
    /// own beyond the numeric range check below).</summary>
    public const int MaxNrStringLength = 8;

    /// <summary>`Main.cpp:6940`'s `d &lt; 4096` bound.</summary>
    public const int CompactNrUpperBound = 4096;

    /// <summary>`Main.cpp:6940`'s full 5-condition predicate, verbatim -- do not simplify to a bare
    /// magnitude check, that was this plan's own round-1 mistake. <paramref name="remainder"/> is
    /// the NR/RST text AFTER skipping the first 3 characters (the RST report digits, `p += 3` at
    /// `Main.cpp:6937`) and after the caller has already applied the `&gt;= '0'` char-set filter
    /// (`Main.cpp:6931`) -- see <see cref="FilterNrRstChars"/>.</summary>
    public static bool IsCompactEligible(string remainder, out uint value)
    {
        value = 0;

        // Main.cpp:6936/6938: `l = strlen(p)` is measured on the FULL remainder, once, before the
        // numeric parse below -- both places `l` is used in the source refer to this same length,
        // not a re-derived digit-only count.
        var l = remainder.Length;
        if (l < 3)
        {
            return false;
        }

        // ComLib.cpp:1706-1712: IsAlphas returns true if the string contains ANY alphabetic
        // character anywhere (first isalpha found), not "is entirely alphabetic". `!IsAlphas`
        // means NONE of the characters are letters.
        if (remainder.Any(char.IsAsciiLetter))
        {
            return false;
        }

        // sscanf(p, "%u", &d): parses a LEADING run of digits and stops at the first non-digit --
        // it does NOT require the whole string to be numeric, unlike a strict whole-string parse.
        // Trailing non-digit, non-letter characters (e.g. ':', ';') are silently ignored by sscanf
        // but still counted in `l` above.
        var digitPrefixLength = 0;
        while (digitPrefixLength < remainder.Length && char.IsAsciiDigit(remainder[digitPrefixLength]))
        {
            digitPrefixLength++;
        }

        // Code-review finding: legacy's sscanf("%u", &d) on a >= 10-digit run could overflow an
        // unsigned int and wrap (Borland CRT behavior, implementation-defined, not independently
        // verified) rather than fail -- an absurdly long RST/NR field could theoretically go
        // "compact" in legacy where this port instead falls through to the string form. The
        // string-form output is still a correct, decodable packet either way; this port's
        // rejection is the safer divergence, not a bug, for input no real RST field would contain.
        if (digitPrefixLength == 0
            || !uint.TryParse(remainder[..digitPrefixLength], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        if (value >= CompactNrUpperBound)
        {
            // Nit fixed (Tier A Batch 8 chunk 8b): TryParse above already wrote a real parsed value
            // into `value` before this check, so a naive `return false;` here would leave `value`
            // non-zero on a false return -- unlike every OTHER false path in this method. No current
            // caller reads `value` when this returns false, but that's a caller-discipline
            // coincidence, not a contract this method itself enforced.
            value = 0;
            return false;
        }

        // The round-trip condition: RX renders a compact NR back out via sprintf("%03u", ...), so
        // compact is only legal when that exactly reproduces `remainder` -- i.e. `l` (the FULL
        // remainder length, including any trailing non-digit chars sscanf ignored) is under 4
        // characters (any value that short round-trips through %03u's zero-padding), or, at 4+
        // characters, the value itself is >= 1000 (so %03u doesn't need to pad and naturally
        // produces the same digit count).
        if (l < 4 || value >= 1000)
        {
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>`Main.cpp:6930-6932`: keeps only bytes `&gt;= '0'`. Signed-`char` comparison in the
    /// original C++ -- bytes `0x80`-`0xFF` are negative there and get dropped by the filter; the
    /// explicit `&lt;= 0x7F` upper bound here reproduces that (a bare `&gt;= '0'` on an unsigned
    /// type would keep them instead, a numeric-fidelity mismatch).
    /// Known, accepted divergence (code-review finding, not fixed -- low-severity, garbage-either-
    /// way): legacy filters CP932 BYTES of the raw text; this filters UTF-16 CHARS. A double-byte
    /// CP932 character's trail byte can happen to land &gt;= 0x30 and survive legacy's filter (e.g.
    /// a lead byte 0x81 dropped as negative, trail byte 0x40='@' kept) while this port drops the
    /// whole character -- only reachable if a user types non-ASCII into the RST-exchange field,
    /// where the resulting NR/RST packet is meaningless either way. Not worth a byte-level rewrite
    /// for a field real RST-exchange values (plain digits) never actually exercise.</summary>
    public static string FilterNrRstChars(string rawText) =>
        new(rawText.Where(ch => ch is >= '0' and <= (char)0x7F).ToArray());
}
