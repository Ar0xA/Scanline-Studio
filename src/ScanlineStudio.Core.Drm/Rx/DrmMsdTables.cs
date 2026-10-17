namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>The multi-stage decoder's tables, <c>msd_hard.h</c> (drmrx/msd_hard.h:1-47; Schorr, Kaiserslautern,
/// GPL-2.0-or-later per msd_hard_sdc.h, which declares the same tables), the Viterbi coder output table
/// (drmrx/viterbi_decode.h:85-86) and the code-rate tables of <c>channel_decoding</c> (drmrx/channeldecode.cpp:82-89).</summary>
/// <remarks>A puncturing row is QSSTV's run-length pattern: entry 0 is the negative wrap step for the traceback,
/// then alternating (pattern, step) pairs; the decoder walks it with an index exactly as QSSTV walks its
/// <c>signed char*</c>.</remarks>
internal static class DrmMsdTables
{
    public const int PuncturingRowLength = 17;
    public const int TailPuncturingRowLength = 13;

    /// <summary><c>puncturing[13][17]</c> (msd_hard.h:1-13), row-major, zero-filled as in C.</summary>
    public static ReadOnlySpan<sbyte> Puncturing =>
    [
        0, 15, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        -4, 15, 2, 7, 2, 7, -4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        -6, 7, 2, 7, 2, 7, 2, 3, -6, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        -6, 3, 2, 5, 2, 3, 2, 1, -6, 0, 0, 0, 0, 0, 0, 0, 0,
        -4, 3, 2, 1, 2, 3, -4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        -2, 3, 2, 1, -2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        -14, 3, 2, 1, 2, 1, 2, 3, 2, 1, 2, 1, 2, 3, 2, 1, -14,
        -4, 3, 2, 1, 2, 1, -4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        -6, 3, 2, 1, 2, 1, 2, 1, -6, 0, 0, 0, 0, 0, 0, 0, 0,
        -12, 3, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1, -12, 0, 0,
        -14, 3, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1, -14,
    ];

    /// <summary><c>int RY[13]</c> (msd_hard.h:18).</summary>
    public static ReadOnlySpan<int> RyInt => [4, 10, 3, 11, 2, 7, 5, 3, 11, 4, 5, 8, 9];

    /// <summary><c>tailpuncturing[12][13]</c> (msd_hard.h:19-30), row-major.</summary>
    public static ReadOnlySpan<sbyte> TailPuncturing =>
    [
        -10, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, -10,
        -10, 7, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, -10,
        -10, 7, 2, 3, 2, 3, 2, 7, 2, 3, 2, 3, -10,
        -10, 7, 2, 7, 2, 3, 2, 7, 2, 3, 2, 3, -10,
        -10, 7, 2, 7, 2, 3, 2, 7, 2, 7, 2, 3, -10,
        -10, 7, 2, 7, 2, 7, 2, 7, 2, 7, 2, 3, -10,
        -10, 7, 2, 7, 2, 7, 2, 7, 2, 7, 2, 7, -10,
        -10, 15, 2, 7, 2, 7, 2, 7, 2, 7, 2, 7, -10,
        -10, 15, 2, 7, 2, 7, 2, 15, 2, 7, 2, 7, -10,
        -10, 15, 2, 15, 2, 7, 2, 15, 2, 7, 2, 7, -10,
        -10, 15, 2, 15, 2, 7, 2, 15, 2, 7, 2, 15, -10,
        -10, 15, 2, 15, 2, 15, 2, 15, 2, 7, 2, 15, -10,
    ];

    // SQRT2/SQRT10/SQRT42 are float literals (msd_hard.h:33-35), so every entry is a float division.
    private const float Sqrt2 = 1.41421356237310F;
    private const float Sqrt10 = 3.16227766016838F;
    private const float Sqrt42 = 6.48074069840786F;

    /// <summary><c>float partitioning[4][8]</c> (msd_hard.h:36-43), row-major, zero-filled as in C: 64-QAM SM,
    /// 64-QAM block (HMmix only, unused by QSSTV), 16-QAM SM, 4-QAM.</summary>
    public static readonly float[] Partitioning =
    [
        7 / Sqrt42, 5 / Sqrt42, 3 / Sqrt42, 1 / Sqrt42, -1 / Sqrt42, -3 / Sqrt42, -5 / Sqrt42, -7 / Sqrt42,
        7 / Sqrt42, -1 / Sqrt42, 5 / Sqrt42, -3 / Sqrt42, 3 / Sqrt42, -5 / Sqrt42, 1 / Sqrt42, -7 / Sqrt42,
        3 / Sqrt10, 1 / Sqrt10, -1 / Sqrt10, -3 / Sqrt10, 0, 0, 0, 0,
        1 / Sqrt2, -1 / Sqrt2, 0, 0, 0, 0, 0, 0,
    ];

    /// <summary><c>CODER_OUTPUT[32]</c> (viterbi_decode.h:85-86): coder output for a zero input per butterfly.</summary>
    public static ReadOnlySpan<byte> CoderOutput =>
        [0, 6, 3, 5, 3, 5, 0, 6, 4, 2, 7, 1, 7, 1, 4, 2, 1, 7, 2, 4, 2, 4, 1, 7, 5, 3, 6, 0, 6, 0, 5, 3];

    /// <summary><c>static double RX[13]</c> (channeldecode.cpp:82).</summary>
    public static ReadOnlySpan<double> Rx => [1, 3, 1, 4, 1, 4, 3, 2, 8, 3, 4, 7, 8];

    /// <summary><c>static double RY[13]</c> (channeldecode.cpp:83).</summary>
    public static ReadOnlySpan<double> Ry => [4, 10, 3, 11, 2, 7, 5, 3, 11, 4, 5, 8, 9];

    /// <summary><c>RatesSM16[2][2]</c> (channeldecode.cpp:86), row-major.</summary>
    public static ReadOnlySpan<int> RatesSm16 => [3, 8, 5, 10];

    /// <summary><c>RatesSM64[4][3]</c> (channeldecode.cpp:87-89), row-major.</summary>
    public static ReadOnlySpan<int> RatesSm64 => [1, 5, 10, 3, 8, 11, 5, 10, 12, 8, 11, 13];
}
