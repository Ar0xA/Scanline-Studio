using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Cw;

/// <summary>The RX-side inverse (dit/dah pattern -&gt; character) lookup, built once from
/// <see cref="CwMorseTable.Table"/> -- a distinct artifact from that table itself (fsk_cwid.md §8.3),
/// not a renamed duplicate of it. New capability, not a legacy port -- YONIQ never decoded CW on
/// receive, so there is no source function this inversion is verified against; its correctness rests
/// entirely on <see cref="CwMorseTable"/> (a verified TX-side port) and the SAME bit-extraction
/// algorithm <c>ScanlineStudio.Core.Sstv.CwMorseGenerator.GenerateChar</c> already uses (low byte of
/// a table entry is the element count; walked MSB-first, bit=1 -&gt; dit, bit=0 -&gt; dah).</summary>
internal static class MorseAlphabet
{
    private static readonly Dictionary<string, char> PatternToChar = BuildInverseTable();

    private static Dictionary<string, char> BuildInverseTable()
    {
        var map = new Dictionary<string, char>();
        var table = CwMorseTable.Table;
        for (var i = 0; i < table.Length; i++)
        {
            var entry = table[i];
            if (entry == 0)
            {
                // Empty table slots (':', ';', '<') -- no real character, nothing to add.
                continue;
            }

            // CwMorseTable.Table's own layout: index i covers ASCII '0'+i, i.e. '0'-'Z' (0x30-0x5A).
            var c = (char)('0' + i);
            var pattern = PatternFromTableEntry(entry);

            // '.' encodes to the SAME bit pattern as 'R' (CwMorseGenerator.GenerateChar's own
            // '.' -> 'R' special case runs BEFORE table lookup on TX) -- a real, documented
            // ambiguity (CwMorseTable's own doc comment), not a gap introduced here. This inverse
            // lookup always resolves that pattern to 'R', the character that actually owns the
            // table slot; a genuine '.' in transmitted CW-ID text is indistinguishable from an 'R'
            // on decode, by legacy's own design.
            map[pattern] = c;
        }

        // '/' has NO table slot -- CwMorseGenerator.GenerateChar special-cases it BEFORE the table
        // lookup with a literal bit pattern (0x6805) that iterating the table above can never
        // discover on its own; added by hand to close that gap, same literal value.
        map[PatternFromTableEntry(0x6805)] = '/';

        return map;
    }

    // Mirrors CwMorseGenerator.GenerateChar's own element-walk exactly (low byte = element count,
    // then MSB-first: bit=1 -> dit '.', bit=0 -> dah '-') so the two can never silently disagree on
    // what a given table entry actually spells out.
    private static string PatternFromTableEntry(int entry)
    {
        var elementCount = entry & 0x00ff;
        var pattern = new char[elementCount];
        for (var i = 0; i < elementCount; i++)
        {
            pattern[i] = (entry & 0x8000) != 0 ? '.' : '-';
            entry <<= 1;
        }

        return new string(pattern);
    }

    /// <summary>Looks up a decoded dit/dah pattern (e.g. <c>"-.."</c>) against the Morse table,
    /// returning <see langword="null"/> if the pattern has no match -- an unmatched pattern becomes
    /// <see cref="Abstractions.Cw.CwDecodedCharacter.IsUnknown"/> at the caller (fsk_cwid.md §8.4
    /// step 6), never a thrown exception; a real CW ID can contain noise-induced or mis-timed
    /// elements that don't spell a real character.</summary>
    public static char? Lookup(string pattern) => PatternToChar.TryGetValue(pattern, out var c) ? c : null;
}
