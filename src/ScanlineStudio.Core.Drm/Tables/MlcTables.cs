namespace ScanlineStudio.Core.Drm.Tables;

/// <summary>Multi-level coder tables ported from QSSTV drmtx/common/tables/TableMLC.h.</summary>
internal static class MlcTables
{
    /// <summary>MC_NUM_OUTPUT_BITS_PER_STEP (TableMLC.h:45).</summary>
    public const int NumOutputBitsPerStep = 4;

    /// <summary>MC_CONSTRAINT_LENGTH (TableMLC.h:53).</summary>
    public const int ConstraintLength = 7;

    /// <summary>MC_NUM_STATES (TableMLC.h:59).</summary>
    public const int NumStates = 1 << (ConstraintLength - 1);

    /// <summary>MC_NUM_OUTPUT_COMBINATIONS (TableMLC.h:60).</summary>
    public const int NumOutputCombinations = 1 << NumOutputBitsPerStep;

    /// <summary>MC_MAX_NUM_LEVELS (TableMLC.h:63).</summary>
    public const int MaxNumLevels = 6;

    /// <summary>LENGTH_TAIL_BIT_PAT (TableMLC.h:287).</summary>
    public const int TailBitPatternLength = 6;

    // Puncturing pattern types (TableMLC.h:68-73).
    public const int PpType0000 = 0;
    public const int PpType1111 = 1;
    public const int PpType0111 = 2;
    public const int PpType0011 = 3;
    public const int PpType0001 = 4;
    public const int PpType0101 = 5;

    /// <summary>Number of rows in <see cref="PuncturingPatterns"/> (TableMLC.h:76).</summary>
    public const int NumPuncturingPatterns = 13;

    /// <summary>Row width of <see cref="PuncturingPatterns"/>: {groups, ones, 8 patterns}.</summary>
    public const int PuncturingPatternRowLength = 10;

    /// <summary>Number of rows in <see cref="TailBitPatterns"/> (TableMLC.h:288).</summary>
    public const int NumTailBitPatterns = 12;

    /// <summary>Generator polynomials, bit-reversed octal as in QSSTV: 0155, 0117, 0123, 0155
    /// (TableMLC.h:46-51).</summary>
    public static ReadOnlySpan<byte> GeneratorMatrix => [0x6D, 0x4F, 0x53, 0x6D];

    /// <summary>iPuncturingPatterns[13][10], row-major: {a = number of groups, b = number of ones,
    /// c.. = PP_TYPE per group} (TableMLC.h:76-284). Row index is the code rate index.</summary>
    public static ReadOnlySpan<int> PuncturingPatterns =>
    [
        1, 4, PpType1111, PpType0000, PpType0000, PpType0000, PpType0000, PpType0000, PpType0000, PpType0000,
        3, 10, PpType1111, PpType0111, PpType0111, PpType0000, PpType0000, PpType0000, PpType0000, PpType0000,
        1, 3, PpType0111, PpType0000, PpType0000, PpType0000, PpType0000, PpType0000, PpType0000, PpType0000,
        4, 11, PpType0111, PpType0111, PpType0111, PpType0011, PpType0000, PpType0000, PpType0000, PpType0000,
        1, 2, PpType0011, PpType0000, PpType0000, PpType0000, PpType0000, PpType0000, PpType0000, PpType0000,
        4, 7, PpType0011, PpType0101, PpType0011, PpType0001, PpType0000, PpType0000, PpType0000, PpType0000,
        3, 5, PpType0011, PpType0001, PpType0011, PpType0000, PpType0000, PpType0000, PpType0000, PpType0000,
        2, 3, PpType0011, PpType0001, PpType0000, PpType0000, PpType0000, PpType0000, PpType0000, PpType0000,
        8, 11, PpType0011, PpType0001, PpType0001, PpType0011, PpType0001, PpType0001, PpType0011, PpType0001,
        3, 4, PpType0011, PpType0001, PpType0001, PpType0000, PpType0000, PpType0000, PpType0000, PpType0000,
        4, 5, PpType0011, PpType0001, PpType0001, PpType0001, PpType0000, PpType0000, PpType0000, PpType0000,
        7, 8, PpType0011, PpType0001, PpType0001, PpType0001, PpType0001, PpType0001, PpType0001, PpType0000,
        8, 9, PpType0011, PpType0001, PpType0001, PpType0001, PpType0001, PpType0001, PpType0001, PpType0001,
    ];

    /// <summary>iPunctPatTailbits[12][6], row-major (TableMLC.h:288-444).</summary>
    public static ReadOnlySpan<int> TailBitPatterns =>
    [
        PpType0011, PpType0011, PpType0011, PpType0011, PpType0011, PpType0011,
        PpType0111, PpType0011, PpType0011, PpType0011, PpType0011, PpType0011,
        PpType0111, PpType0011, PpType0011, PpType0111, PpType0011, PpType0011,
        PpType0111, PpType0111, PpType0011, PpType0111, PpType0011, PpType0011,
        PpType0111, PpType0111, PpType0011, PpType0111, PpType0111, PpType0011,
        PpType0111, PpType0111, PpType0111, PpType0111, PpType0111, PpType0011,
        PpType0111, PpType0111, PpType0111, PpType0111, PpType0111, PpType0111,
        PpType1111, PpType0111, PpType0111, PpType0111, PpType0111, PpType0111,
        PpType1111, PpType0111, PpType0111, PpType1111, PpType0111, PpType0111,
        PpType1111, PpType1111, PpType0111, PpType1111, PpType0111, PpType0111,
        PpType1111, PpType1111, PpType0111, PpType1111, PpType0111, PpType1111,
        PpType1111, PpType1111, PpType1111, PpType1111, PpType0111, PpType1111,
    ];

    /// <summary>iCodRateCombMSC4SM: R_0 (TableMLC.h:451-453).</summary>
    public const int CodeRateCombMsc4Sm = 6;

    /// <summary>iCodRateCombMSC16SM[2][3], row = protection level: {R_0, R_1, RY_Icm}
    /// (TableMLC.h:455-458).</summary>
    public static ReadOnlySpan<int> CodeRateCombMsc16Sm => [2, 7, 3, 4, 9, 4];

    /// <summary>iCodRateCombMSC64SM[4][4], row = protection level: {R_0, R_1, R_2, RY_Icm}
    /// (TableMLC.h:461-466).</summary>
    public static ReadOnlySpan<int> CodeRateCombMsc64Sm => [0, 4, 9, 4, 2, 7, 10, 15, 4, 9, 11, 8, 7, 10, 12, 45];

    /// <summary>iCodRateCombFDC4SM: R_0 for FAC (TableMLC.h:469-471).</summary>
    public const int CodeRateCombFdc4Sm = 6;

    /// <summary>iInterlSequ4SM: bit-interleaver type per level, -1 = none (TableMLC.h:483).</summary>
    public static ReadOnlySpan<int> InterleaverSequence4Sm => [1, -1, -1, -1, -1, -1];

    /// <summary>iInterlSequ16SM (TableMLC.h:484).</summary>
    public static ReadOnlySpan<int> InterleaverSequence16Sm => [0, 1, -1, -1, -1, -1];

    /// <summary>iInterlSequ64SM (TableMLC.h:485).</summary>
    public static ReadOnlySpan<int> InterleaverSequence64Sm => [-1, 0, 1, -1, -1, -1];
}
