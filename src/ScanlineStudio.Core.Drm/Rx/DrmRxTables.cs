namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>The QSSTV RX demodulator's own mode tables, copied verbatim (drmrx/drmdefs.h:18-33,
/// drmrx/demodulator.cpp:17-19, drmrx/demodulator.h:9-12, drmrx/drm.cpp:28-60). Robustness index 0/1/2 is
/// QSSTV's A/B/E; QSSTV's index 2 is labelled "C" in drmdefs.h but carries mode E's Ts/Tu. Index 3 ("D") is
/// never detected (getmode.cpp:114) and is kept only so table shapes match.</summary>
internal static class DrmRxTables
{
    /// <summary>DRMNUMMODES (demodulator.h:12).</summary>
    public const int ModeCount = 4;

    /// <summary>getmode's "no reliable mode" result (getmode.cpp:225).</summary>
    public const int NoMode = 99;

    /// <summary>Tu_A (drmdefs.h:20): the largest useful-symbol length, which sizes the symbol buffers.</summary>
    public const int TuMax = 288;

    /// <summary>N_symbols_mode_detection (demodulator.cpp:44).</summary>
    public const int SymbolsModeDetection = 20;

    /// <summary>N_symbols_frequency_pilot_search (demodulator.cpp:45, drm.cpp:48).</summary>
    public const int SymbolsFrequencyPilotSearch = 15;

    /// <summary>The fixed sample stride getmode and timeSync use for the mode-detection window (demodulator.cpp:48,180).</summary>
    public const int ModeDetectionStride = 320;

    /// <summary>The time-reference cell table width (drm.cpp:29; getsymbolidx is always called with 21, demodulator.cpp:454).</summary>
    public const int TimeRefCellColumns = 21;

    /// <summary>SNR_TIMEOUT (demodulator.h:9).</summary>
    public const int SnrTimeout = 10;

    /// <summary>FACVALIDCNTR (demodulator.h:10).</summary>
    public const int FacValidCounter = 10;

    /// <summary>SNR_MIN_DB (demodulator.h:11).</summary>
    public const int SnrMinDb = 3;

    /// <summary>Ts_list (demodulator.cpp:17).</summary>
    public static ReadOnlySpan<int> Ts => [320, 320, 240, 200];

    /// <summary>Tu_list (demodulator.cpp:18).</summary>
    public static ReadOnlySpan<int> Tu => [288, 256, 160, 112];

    /// <summary>Tg_list (demodulator.cpp:19).</summary>
    public static ReadOnlySpan<int> Tg => [32, 64, 64, 88];

    /// <summary>symbols_per_frame_list (drm.cpp:28).</summary>
    public static ReadOnlySpan<int> SymbolsPerFrame => [15, 15, 20, 24];

    /// <summary>y_list (drm.cpp:44).</summary>
    public static ReadOnlySpan<int> Y => [5, 3, 4, 3];

    /// <summary>symbols_per_2D_window_list (drm.cpp:45).</summary>
    public static ReadOnlySpan<int> SymbolsPer2DWindow => [10, 6, 8, 6];

    /// <summary>symbols_to_delay_list (drm.cpp:46).</summary>
    public static ReadOnlySpan<int> SymbolsToDelay => [5, 3, 4, 3];

    /// <summary>no_of_unused_carriers_list (drm.cpp:60).</summary>
    public static ReadOnlySpan<int> UnusedCarriers => [2, 1, 1, 1];

    /// <summary>time_ref_cells_k_list (drm.cpp:29-35), row-major [mode][21].</summary>
    public static ReadOnlySpan<int> TimeRefCellsK =>
    [
        6, 7, 11, 12, 15, 16, 23, 29, 30, 33, 34, 38, 39, 41, 45, 46, 0, 0, 0, 0, 0,
        6, 10, 11, 14, 17, 18, 27, 28, 30, 33, 34, 38, 40, 41, 44, 0, 0, 0, 0, 0, 0,
        7, 8, 13, 14, 21, 22, 26, 27, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 0, 0, 0, 0, 0,
    ];

    /// <summary>time_ref_cells_theta_1024_list (drm.cpp:36-42), row-major [mode][21].</summary>
    public static ReadOnlySpan<int> TimeRefCellsTheta1024 =>
    [
        973, 205, 717, 264, 357, 357, 952, 440, 856, 88, 88, 68, 836, 836, 836, 1008, 0, 0, 0, 0, 0,
        304, 331, 108, 620, 192, 704, 44, 432, 588, 844, 651, 651, 651, 460, 950, 0, 0, 0, 0, 0, 0,
        432, 331, 108, 620, 192, 704, 44, 304, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 0, 0, 0, 0, 0,
    ];

    /// <summary>K_min_K_max_list[0] (drm.cpp:49-53): K_min per [spectrum occupancy + 6·mode].</summary>
    public static ReadOnlySpan<int> KMin =>
        [2, 2, -102, -114, -98, -110, 1, 1, -91, -103, -87, -99, 1, 1, 0, -69, 0, -67, 0, 0, 0, -44, 0, -43];

    /// <summary>K_min_K_max_list[1] (drm.cpp:49-53): K_max per [spectrum occupancy + 6·mode].</summary>
    public static ReadOnlySpan<int> KMax =>
        [54, 58, 102, 114, 314, 350, 45, 51, 91, 103, 279, 311, 29, 31, 0, 69, 0, 213, 0, 0, 0, 44, 0, 135];
}
