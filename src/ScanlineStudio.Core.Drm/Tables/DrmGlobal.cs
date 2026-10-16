namespace ScanlineStudio.Core.Drm.Tables;

/// <summary>HamDRM robustness modes. Values match QSSTV's <c>ERobMode</c> order
/// (drmtx/common/GlobalDefinitions.h:168-169); mode D is not used by HamDRM.</summary>
internal enum RobustnessMode
{
    A = 0,
    B = 1,
    E = 2,
}

/// <summary>HamDRM spectrum occupancy (GlobalDefinitions.h:167). Only SO_0 and SO_1 are used.</summary>
internal enum SpectrumOccupancy
{
    So0 = 0,
    So1 = 1,
}

/// <summary>The parts of QSSTV's TableDRMGlobal.h that HamDRM uses
/// (drmtx/common/tables/TableDRMGlobal.h:43-70).</summary>
internal static class DrmGlobal
{
    /// <summary>DC carrier position inside the FFT range (TableDRMGlobal.h:43).</summary>
    public const int VirtualIntermediateFrequency = 6000;

    /// <summary>TX sample rate (TableDRMGlobal.h:45).</summary>
    public const int SoundCardSampleRate = 48000;

    /// <summary>TableDRMGlobal.h:50.</summary>
    public const int NumFramesInSuperframe = 3;

    /// <summary>TableDRMGlobal.h:70.</summary>
    public const int NumRobustnessModes = 3;

    /// <summary>Useful-part FFT size at 48 kHz: RMA/RMB/RME_FFT_SIZE_N (TableDRMGlobal.h:52,57,62).</summary>
    public static int FftSize(RobustnessMode mode) => mode switch
    {
        RobustnessMode.A => 1152,
        RobustnessMode.B => 1024,
        RobustnessMode.E => 640,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>Symbols per frame: RM*_NUM_SYM_PER_FRAME (TableDRMGlobal.h:53,58,63).</summary>
    public static int SymbolsPerFrame(RobustnessMode mode) => mode switch
    {
        RobustnessMode.A => 15,
        RobustnessMode.B => 15,
        RobustnessMode.E => 20,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>Guard/useful ratio Tg/Tu as (numerator, denominator):
    /// RM*_ENUM_TG_TU / RM*_DENOM_TG_TU (TableDRMGlobal.h:54-55,59-60,64-65).</summary>
    public static (int Enum, int Denom) GuardRatio(RobustnessMode mode) => mode switch
    {
        RobustnessMode.A => (1, 9),
        RobustnessMode.B => (1, 4),
        RobustnessMode.E => (1, 2),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}
