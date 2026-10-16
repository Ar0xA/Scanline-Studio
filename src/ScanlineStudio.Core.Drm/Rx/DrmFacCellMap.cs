namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>QSSTV RX's FAC cell map, <c>mkfacmap</c> (drmrx/mkfacmap.cpp:30-257): the 45 FAC cell positions of
/// a frame as <c>K_dc + symbol · K_modulo + carrier</c>, in QSSTV's listed order (FAC decoding reads them in
/// this order). Ported from the RX source rather than derived from the TX cell table.</summary>
internal static class DrmFacCellMap
{
    /// <summary>FAC cells per frame for modes A/B/E (mkfacmap.cpp:39,87,135).</summary>
    public const int CellCount = 45;

    // (symbol, carrier) pairs per mode, mkfacmap.cpp:41-85 (A), 89-133 (B), 137-181 (E).
    private static ReadOnlySpan<byte> ModeA =>
    [
        1, 10, 1, 22, 1, 30, 1, 50, 2, 14, 2, 26, 2, 34, 3, 18, 3, 30, 3, 38, 4, 22, 4, 34, 4, 42, 5, 18, 5, 26,
        5, 38, 5, 46, 6, 22, 6, 30, 6, 42, 6, 50, 7, 26, 7, 34, 7, 46, 8, 10, 8, 30, 8, 38, 8, 50, 9, 14, 9, 34,
        9, 42, 10, 18, 10, 38, 10, 46, 11, 10, 11, 22, 11, 42, 11, 50, 12, 14, 12, 26, 12, 46, 13, 18, 13, 30, 14, 22, 14, 34,
    ];

    private static ReadOnlySpan<byte> ModeB =>
    [
        0, 21, 1, 11, 1, 23, 1, 35, 2, 13, 2, 25, 2, 37, 3, 15, 3, 27, 3, 39, 4, 5, 4, 17, 4, 29, 4, 41, 5, 7,
        5, 19, 5, 31, 6, 9, 6, 21, 6, 33, 7, 11, 7, 23, 7, 35, 8, 13, 8, 25, 8, 37, 9, 15, 9, 27, 9, 39, 10, 5,
        10, 17, 10, 29, 10, 41, 11, 7, 11, 19, 11, 31, 12, 9, 12, 21, 12, 33, 13, 11, 13, 23, 13, 35, 14, 13, 14, 25, 14, 37,
    ];

    private static ReadOnlySpan<byte> ModeE =>
    [
        1, 7, 1, 23, 2, 8, 2, 16, 2, 24, 3, 9, 3, 17, 4, 10, 4, 18, 5, 11, 5, 19, 6, 4, 6, 12, 7, 13, 7, 21,
        8, 6, 8, 14, 8, 22, 9, 7, 9, 23, 10, 8, 10, 16, 10, 24, 11, 9, 11, 13, 11, 17, 12, 10, 12, 18, 13, 11, 13, 19,
        14, 4, 14, 12, 14, 16, 15, 13, 15, 21, 16, 6, 16, 14, 16, 22, 17, 7, 17, 23, 18, 8, 18, 16, 18, 24, 19, 9, 19, 17,
    ];

    /// <summary><c>mkfacmap(robustness_mode, K_dc, K_modulo, FACmap)</c>: fills <paramref name="map"/> and returns lFAC.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Mode D (3), which QSSTV never detects (getmode.cpp:114), or any other value.</exception>
    public static int Build(int robustnessMode, int kDc, int kModulo, Span<int> map)
    {
        ReadOnlySpan<byte> pairs = robustnessMode switch
        {
            0 => ModeA,
            1 => ModeB,
            2 => ModeE,
            _ => throw new ArgumentOutOfRangeException(nameof(robustnessMode), robustnessMode, "Only modes A, B and E are received."),
        };
        for (int i = 0; i < CellCount; i++)
            map[i] = kDc + pairs[2 * i] * kModulo + pairs[2 * i + 1];
        return CellCount;
    }
}
