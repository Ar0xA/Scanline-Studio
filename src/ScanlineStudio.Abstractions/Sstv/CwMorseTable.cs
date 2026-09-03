namespace ScanlineStudio.Abstractions.Sstv;

/// <summary>Direct port of legacy <c>CSSTVMOD::WriteCWID</c>'s own bit-packed dot/dash table
/// (`sstv.cpp:2951-2966`) -- one element (tone) + inter-element gap per bit, MSB-first: bit=1 -&gt;
/// dit (<c>dot</c> length), bit=0 -&gt; dah (<c>dot*3</c> length); the low byte of a table entry is
/// the element count. See <c>ScanlineStudio.Core.Sstv.CwMorseGenerator</c> (the TX-side encoder that
/// consumes this table) for the full legacy citation trail and the special cases NOT captured by the
/// table itself: '.' is remapped to 'R' before lookup (`sstv.cpp:2972-2975`) -- so <see cref="Table"/>
/// alone cannot distinguish an encoded 'R' from an encoded '.', a real ambiguity any INVERSE
/// (decode-side) consumer of this table must account for; '/' uses a literal bit pattern
/// <c>0x6805</c> not present anywhere in <see cref="Table"/> at all.
///
/// fsk_cwid.md B-P1 layering move: lives here (not <c>ScanlineStudio.Core.Sstv</c>, where the table
/// originated) so <c>ScanlineStudio.Core.Cw</c>'s own <c>MorseAlphabet</c> (the RX-side inverse
/// lookup) can share the identical table data without a <c>Core.Cw -&gt; Core.Sstv</c> project
/// reference -- one copy, so TX encode and RX decode can never quietly drift onto different tables.
/// <c>CwMorseGenerator</c> itself was changed to reference this relocated table instead of its own
/// former private field; its own encoding BEHAVIOR (the '.'/'/'/masking special-casing above) is
/// unchanged by this move.</summary>
public static class CwMorseTable
{
    // sstv.cpp:2953-2966, verbatim byte values.
    public static readonly ushort[] Table =
    [
        // 0       1       2       3       4       5       6       7
        0x0005, 0x8005, 0xc005, 0xe005, 0xf005, 0xf805, 0x7805, 0x3805,
        // 8       9       :       ;       <       =       >       ?
        0x1805, 0x0805, 0x0000, 0x0000, 0x0000, 0x7005, 0xa805, 0xcc06,
        // @       A       B       C       D       E       F       G
        0x0000, 0x8002, 0x7004, 0x5004, 0x6003, 0x8001, 0xd004, 0x2003,
        // H       I       J       K       L       M       N       O
        0xf004, 0xc002, 0x8004, 0x4003, 0xb004, 0x0002, 0x4002, 0x0003,
        // P       Q       R       S       T       U       V       W
        0x9004, 0x2004, 0xa003, 0xe003, 0x0001, 0xc003, 0xe004, 0x8003,
        // X       Y       Z
        0x6004, 0x4004, 0x3004,
    ];
}
