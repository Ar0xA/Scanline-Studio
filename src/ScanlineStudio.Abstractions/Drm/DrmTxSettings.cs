namespace ScanlineStudio.Abstractions.Drm;

/// <summary>DRM robustness mode. QSSTV's TX offers A, B and E (drmtransmitter.cpp:53-60).</summary>
public enum DrmRobustnessMode
{
    A,
    B,
    E,
}

/// <summary>Spectrum occupancy, named by QSSTV's labels (txwidget.ui: "2.2 kHz", "2.5 kHz").</summary>
public enum DrmBandwidth
{
    /// <summary>SO_0.</summary>
    Hz2200,

    /// <summary>SO_1.</summary>
    Hz2500,
}

/// <summary>MSC constellation.</summary>
public enum DrmQamOrder
{
    Qam4,
    Qam16,
    Qam64,
}

/// <summary>MSC protection level. QSSTV labels <see cref="Low"/> "Norm" (drmtransmitter.cpp:99-107).</summary>
public enum DrmProtectionLevel
{
    High,
    Low,
}

/// <summary>Symbol interleaver depth, named by what is really sent. QSSTV's UI labels the long
/// interleaver "Short" (txwidget.ui:1083-1092, drmtransmitter.cpp:76-84).</summary>
public enum DrmInterleaving
{
    LongInterleaver,
    ShortInterleaver,
}

/// <summary>Reed-Solomon file protection, QSSTV <c>eRSType</c> (reedsolomoncoder.h:8).</summary>
public enum DrmReedSolomonLevel
{
    None,
    Rs1,
    Rs2,
    Rs3,
    Rs4,
}

/// <summary>Everything one DRM (HamDRM/EasyPal-compatible) image transmission needs.</summary>
/// <param name="Callsign">Sent in FAC as Latin-1, at most 9 characters (FAC.cpp:152-171).</param>
/// <param name="VoxTone">Sends QSSTV's 1 s, 1700 Hz VOX tone before the DRM frames (txfunctions.cpp:207).</param>
/// <param name="TargetJp2Bytes">QSSTV's target compressed size; the JPEG 2000 ratio is derived from it
/// (imageviewer.cpp:1049-1054). Must be positive.</param>
/// <param name="LevelOffsetDb">Our own DRM drive offset, −20..0 dB, applied on top of Pwr. Not a QSSTV setting:
/// it lets an operator trim DRM against SSTV without moving Pwr. The service clamps it (<see cref="ClampLevelOffsetDb"/>).</param>
public sealed record DrmTxSettings(
    DrmRobustnessMode Mode,
    DrmBandwidth Bandwidth,
    DrmQamOrder Qam,
    DrmProtectionLevel Protection,
    DrmInterleaving Interleave,
    DrmReedSolomonLevel ReedSolomon,
    string Callsign,
    bool VoxTone,
    int TargetJp2Bytes,
    double LevelOffsetDb = 0)
{
    public const double MinLevelOffsetDb = -20;

    public const double MaxLevelOffsetDb = 0;

    /// <summary>−20..0 dB; NaN or infinity gives 0.</summary>
    public static double ClampLevelOffsetDb(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, MinLevelOffsetDb, MaxLevelOffsetDb) : 0;
}
