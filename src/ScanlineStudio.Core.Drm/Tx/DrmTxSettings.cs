using ScanlineStudio.Core.Drm.Tables;

namespace ScanlineStudio.Core.Drm.Tx;

/// <summary>MSC constellation. QSSTV: CS_1_SM / CS_2_SM / CS_3_SM.</summary>
internal enum DrmQam
{
    Qam4,
    Qam16,
    Qam64,
}

/// <summary>MSC protection, named by the <c>MSCPrLe.iPartB</c> value sent in FAC.
/// QSSTV labels index 1 "Norm" (drmtransmitter.cpp:99-107). 4-QAM ignores it in the MSC chain.</summary>
internal enum DrmProtection
{
    /// <summary>iPartB = 0, FAC protection bit 0.</summary>
    High,

    /// <summary>iPartB = 1, FAC protection bit 1.</summary>
    Low,
}

/// <summary>Symbol interleaver depth, named by what is really sent. QSSTV's UI labels index 0
/// "Short", but it runs SI_LONG and FAC sends 0 (drmtransmitter.cpp:76-84, FAC.cpp:86-95).</summary>
internal enum DrmInterleave
{
    /// <summary>SI_LONG, FAC interleaver bit 0. QSSTV index 0.</summary>
    Long,

    /// <summary>SI_SHORT, FAC interleaver bit 1. QSSTV index 1.</summary>
    Short,
}

/// <summary>Reed-Solomon file protection, QSSTV <c>eRSType</c> (reedsolomoncoder.h:8).</summary>
internal enum DrmReedSolomon
{
    None,

    /// <summary>RS(255,224).</summary>
    Rs1,

    /// <summary>RS(255,192).</summary>
    Rs2,

    /// <summary>RS(255,160).</summary>
    Rs3,

    /// <summary>RS(255,128).</summary>
    Rs4,
}

/// <summary>DRM TX settings, by what goes on air. <see cref="Callsign"/> becomes the FAC label.</summary>
internal sealed record DrmTxSettings(
    RobustnessMode Mode,
    SpectrumOccupancy Occupancy,
    DrmQam Qam,
    DrmProtection Protection,
    DrmInterleave Interleave,
    DrmReedSolomon ReedSolomon,
    string Callsign);

/// <summary>QSSTV's <c>drmTxParams</c> indices (drmparams.h:10-19), the form QSSTV's UI, profiles and
/// FIX sessions store. Mapping to <see cref="DrmTxSettings"/> follows drmTransmitter::init
/// (drmtransmitter.cpp:53-108), including its default branches:
/// robMode 0=A, 1=B, other=E; bandwith 0=SO_0, other=SO_1; interleaver 0=Long, other=Short;
/// qam 0=4, 1=16, other=64; protection 1=Low, other=High; reedSolomon 0-4 = None, RS1-RS4.</summary>
internal readonly record struct QsstvDrmTxParams(int RobMode, int Bandwith, int Qam, int Protection, int Interleaver, int ReedSolomon)
{
    public DrmTxSettings ToSettings(string callsign) => new(
        RobMode switch { 0 => RobustnessMode.A, 1 => RobustnessMode.B, _ => RobustnessMode.E },
        Bandwith == 0 ? SpectrumOccupancy.So0 : SpectrumOccupancy.So1,
        Qam switch { 0 => DrmQam.Qam4, 1 => DrmQam.Qam16, _ => DrmQam.Qam64 },
        Protection == 1 ? DrmProtection.Low : DrmProtection.High,
        Interleaver == 0 ? DrmInterleave.Long : DrmInterleave.Short,
        ReedSolomon is >= 0 and <= 4
            ? (DrmReedSolomon)ReedSolomon
            : throw new InvalidOperationException($"QSSTV defines RS types 0-4 only (reedsolomoncoder.h:8), got {ReedSolomon}."),
        callsign);

    public static QsstvDrmTxParams FromSettings(DrmTxSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new(
            (int)settings.Mode,
            (int)settings.Occupancy,
            (int)settings.Qam,
            settings.Protection == DrmProtection.Low ? 1 : 0,
            settings.Interleave == DrmInterleave.Long ? 0 : 1,
            (int)settings.ReedSolomon);
    }
}
