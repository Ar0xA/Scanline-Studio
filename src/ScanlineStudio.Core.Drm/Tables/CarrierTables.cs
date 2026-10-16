namespace ScanlineStudio.Core.Drm.Tables;

/// <summary>Carrier, pilot and FAC position tables for modes A/B/E, ported from QSSTV
/// drmtx/common/tables/TableCarrier.h and TableCarMap.h. Pair tables are flattened row-major:
/// element [2*i] is the first column, [2*i+1] the second, as CellMappingTable.cpp indexes them.</summary>
internal static class CarrierTables
{
    /// <summary>NUM_FAC_CELLS (TableCarrier.h:4).</summary>
    public const int NumFacCells = 45;

    /// <summary>NUM_FREQ_PILOTS (TableCarrier.h:67).</summary>
    public const int NumFreqPilots = 3;

    /// <summary>NUM_BOOSTED_SCAT_PILOTS (TableCarMap.h:213).</summary>
    public const int NumBoostedScatPilots = 4;

    // {symbol, carrier} - TableCarrier.h:9-24
    private static ReadOnlySpan<int> FacA =>
    [
        1, 10, 1, 22, 1, 30, 1, 50,
        2, 14, 2, 26, 2, 34,
        3, 18, 3, 30, 3, 38,
        4, 22, 4, 34, 4, 42,
        5, 18, 5, 26, 5, 38, 5, 46,
        6, 22, 6, 30, 6, 42, 6, 50,
        7, 26, 7, 34, 7, 46,
        8, 10, 8, 30, 8, 38, 8, 50,
        9, 14, 9, 34, 9, 42,
        10, 18, 10, 38, 10, 46,
        11, 10, 11, 22, 11, 42, 11, 50,
        12, 14, 12, 26, 12, 46,
        13, 18, 13, 30,
        14, 22, 14, 34,
    ];

    // TableCarrier.h:26-42
    private static ReadOnlySpan<int> FacB =>
    [
        0, 21,
        1, 11, 1, 23, 1, 35,
        2, 13, 2, 25, 2, 37,
        3, 15, 3, 27, 3, 39,
        4, 5, 4, 17, 4, 29, 4, 41,
        5, 7, 5, 19, 5, 31,
        6, 9, 6, 21, 6, 33,
        7, 11, 7, 23, 7, 35,
        8, 13, 8, 25, 8, 37,
        9, 15, 9, 27, 9, 39,
        10, 5, 10, 17, 10, 29, 10, 41,
        11, 7, 11, 19, 11, 31,
        12, 9, 12, 21, 12, 33,
        13, 11, 13, 23, 13, 35,
        14, 13, 14, 25, 14, 37,
    ];

    // TableCarrier.h:44-64
    private static ReadOnlySpan<int> FacE =>
    [
        1, 7, 1, 23,
        2, 8, 2, 16, 2, 24,
        3, 9, 3, 17,
        4, 10, 4, 18,
        5, 11, 5, 19,
        6, 4, 6, 12,
        7, 13, 7, 21,
        8, 6, 8, 14, 8, 22,
        9, 7, 9, 23,
        10, 8, 10, 16, 10, 24,
        11, 9, 11, 13, 11, 17,
        12, 10, 12, 18,
        13, 11, 13, 19,
        14, 4, 14, 12, 14, 16,
        15, 13, 15, 21,
        16, 6, 16, 14, 16, 22,
        17, 7, 17, 23,
        18, 8, 18, 16, 18, 24,
        19, 9, 19, 17,
    ];

    // {carrier, phase/1024} - TableCarrier.h:68-82
    private static ReadOnlySpan<int> FreqPilA => [9, 205, 27, 836, 36, 215];
    private static ReadOnlySpan<int> FreqPilB => [8, 331, 24, 651, 32, 555];
    private static ReadOnlySpan<int> FreqPilE => [5, 788, 15, 1014, 20, 332];

    // [SO][mode A,B,E] - TableCarrier.h:87-95
    private static ReadOnlySpan<int> KminTable => [2, 1, 1, 2, 1, 1];
    private static ReadOnlySpan<int> KmaxTable => [54, 45, 29, 58, 51, 31];

    // {carrier, phase/1024} - TableCarMap.h:93-143
    private static ReadOnlySpan<int> TimePilA =>
    [
        6, 973, 7, 205, 11, 717, 12, 264, 15, 357, 16, 357, 23, 952, 29, 440,
        30, 856, 33, 88, 34, 88, 38, 68, 39, 836, 41, 836, 45, 836, 46, 1008,
    ];

    private static ReadOnlySpan<int> TimePilB =>
    [
        6, 304, 10, 331, 11, 108, 14, 620, 17, 192, 18, 704, 27, 44, 28, 432,
        30, 588, 33, 844, 34, 651, 38, 651, 40, 651, 41, 460, 44, 944,
    ];

    private static ReadOnlySpan<int> TimePilE =>
    [
        7, 432, 8, 331, 13, 108, 14, 620, 21, 192, 22, 704, 26, 44, 27, 304,
    ];

    // {x, y, k_0} - TableCarMap.h:158-162
    private static ReadOnlySpan<int> ScatConstA => [4, 5, 2];
    private static ReadOnlySpan<int> ScatConstB => [2, 3, 1];
    private static ReadOnlySpan<int> ScatConstE => [1, 4, 1];

    // W and Z matrices, row-major [n][m] - TableCarMap.h:164-210
    private static ReadOnlySpan<int> ScatWA => [228, 341, 455, 455, 569, 683, 683, 796, 910, 910, 0, 114, 114, 228, 341];
    private static ReadOnlySpan<int> ScatZA => [0, 81, 248, 18, 106, 106, 122, 116, 31, 129, 129, 39, 33, 32, 111];

    private static ReadOnlySpan<int> ScatWB => [512, 0, 512, 0, 512, 0, 512, 0, 512, 0, 512, 0, 512, 0, 512];
    private static ReadOnlySpan<int> ScatZB => [0, 57, 164, 64, 12, 168, 255, 161, 106, 118, 25, 232, 132, 233, 38];

    private static ReadOnlySpan<int> ScatWE =>
        [512, 0, 512, 0, 512, 0, 512, 0, 512, 0, 512, 0, 512, 0, 512, 0, 512, 0, 512, 0];
    private static ReadOnlySpan<int> ScatZE =>
        [0, 57, 164, 64, 12, 168, 255, 161, 106, 118, 25, 232, 132, 233, 38, 168, 255, 161, 106, 118];

    // Boosted scattered pilot carriers, [SO][4] - TableCarMap.h:214-229
    private static ReadOnlySpan<int> GainA => [2, 4, 50, 54, 2, 6, 54, 58];
    private static ReadOnlySpan<int> GainB => [1, 3, 43, 45, 1, 3, 49, 51];
    private static ReadOnlySpan<int> GainE => [1, 29, 0, 0, 1, 31, 0, 0];

    /// <summary>iTableFACRobMod* as flattened {symbol, carrier} pairs.</summary>
    public static ReadOnlySpan<int> FacPositions(RobustnessMode mode) => mode switch
    {
        RobustnessMode.A => FacA,
        RobustnessMode.B => FacB,
        RobustnessMode.E => FacE,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>iTableFreqPilRobMod* as flattened {carrier, phase} pairs.</summary>
    public static ReadOnlySpan<int> FreqPilots(RobustnessMode mode) => mode switch
    {
        RobustnessMode.A => FreqPilA,
        RobustnessMode.B => FreqPilB,
        RobustnessMode.E => FreqPilE,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>iTableTimePilRobMod* as flattened {carrier, phase} pairs; length/2 = RM*_NUM_TIME_PIL.</summary>
    public static ReadOnlySpan<int> TimePilots(RobustnessMode mode) => mode switch
    {
        RobustnessMode.A => TimePilA,
        RobustnessMode.B => TimePilB,
        RobustnessMode.E => TimePilE,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>iTableCarrierKmin[so][mode].</summary>
    public static int CarrierKmin(RobustnessMode mode, SpectrumOccupancy so) => KminTable[((int)so * 3) + (int)mode];

    /// <summary>iTableCarrierKmax[so][mode].</summary>
    public static int CarrierKmax(RobustnessMode mode, SpectrumOccupancy so) => KmaxTable[((int)so * 3) + (int)mode];

    /// <summary>RM*_SCAT_PIL_FREQ_INT (TableCarMap.h:148,151,154).</summary>
    public static int ScatPilotFreqInterval(RobustnessMode mode) => mode switch
    {
        RobustnessMode.A => 4,
        RobustnessMode.B => 2,
        RobustnessMode.E => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>RM*_SCAT_PIL_TIME_INT (TableCarMap.h:149,152,155).</summary>
    public static int ScatPilotTimeInterval(RobustnessMode mode) => mode switch
    {
        RobustnessMode.A => 5,
        RobustnessMode.B => 3,
        RobustnessMode.E => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>iTableScatPilConstRobMod*: {x, y, k_0}.</summary>
    public static ReadOnlySpan<int> ScatPilotConst(RobustnessMode mode) => mode switch
    {
        RobustnessMode.A => ScatConstA,
        RobustnessMode.B => ScatConstB,
        RobustnessMode.E => ScatConstE,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>SIZE_COL_WZ_ROB_MOD_* (TableCarMap.h:165,183,197).</summary>
    public static int ScatPilotWzColumns(RobustnessMode mode) => mode switch
    {
        RobustnessMode.A => 3,
        RobustnessMode.B => 5,
        RobustnessMode.E => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>iScatPilWRobMod*, row-major.</summary>
    public static ReadOnlySpan<int> ScatPilotW(RobustnessMode mode) => mode switch
    {
        RobustnessMode.A => ScatWA,
        RobustnessMode.B => ScatWB,
        RobustnessMode.E => ScatWE,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>iScatPilZRobMod*, row-major.</summary>
    public static ReadOnlySpan<int> ScatPilotZ(RobustnessMode mode) => mode switch
    {
        RobustnessMode.A => ScatZA,
        RobustnessMode.B => ScatZB,
        RobustnessMode.E => ScatZE,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>iScatPilQRobMod* (TableCarMap.h:180,194,210).</summary>
    public static int ScatPilotQ(RobustnessMode mode) => mode switch
    {
        RobustnessMode.A => 36,
        RobustnessMode.B => 12,
        RobustnessMode.E => 10,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>iScatPilGainRobMod*[so]: the boosted scattered pilot carriers.</summary>
    public static ReadOnlySpan<int> BoostedPilotCarriers(RobustnessMode mode, SpectrumOccupancy so)
    {
        ReadOnlySpan<int> t = mode switch
        {
            RobustnessMode.A => GainA,
            RobustnessMode.B => GainB,
            RobustnessMode.E => GainE,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        return t.Slice((int)so * NumBoostedScatPilots, NumBoostedScatPilots);
    }
}
