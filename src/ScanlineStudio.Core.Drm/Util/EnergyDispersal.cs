namespace ScanlineStudio.Core.Drm.Util;

/// <summary>DRM energy dispersal with the PRBS P(X) = X^9 + X^5 + 1, register initialised to all ones.</summary>
internal static class EnergyDispersal
{
    /// <summary>TX scrambler, port of CEngergyDispersal::ProcessData (drmtx/common/mlc/EnergyDispersal.cpp:36-82).
    /// XORs the PRBS onto one-bit-per-byte data in place. The register restarts at
    /// <paramref name="lengthVspp"/>: bits before it use the VSPP register, the rest the SPP register.</summary>
    public static void Apply(Span<byte> bits, int lengthVspp)
    {
        uint vspp = ~0u;
        uint spp = ~0u;
        for (int i = 0; i < bits.Length; i++)
        {
            ref uint reg = ref i < lengthVspp ? ref vspp : ref spp;
            // Taps 5 and 9 sit at register bits 4 and 8 (shift n-1, EnergyDispersal.cpp:56-57).
            byte prbs = (byte)(((reg >> 4) & 1) ^ ((reg >> 8) & 1));
            reg = (reg << 1) | prbs;
            bits[i] ^= prbs;
        }
    }
}

/// <summary>RX PRBS generator, port of the PRBS_INIT/PRBS_BIT/PRBS_SHIFT macros
/// (drmrx/msdhardmsc.cpp:45-47). Produces the same sequence as <see cref="EnergyDispersal"/>
/// with a right-shifting 9-bit register.</summary>
internal struct RxPrbs
{
    private int _reg;

    /// <summary>PRBS_INIT: reg = 511.</summary>
    public static RxPrbs Create() => new() { _reg = 511 };

    /// <summary>PRBS_BIT: the current output bit.</summary>
    public readonly int Bit => (_reg ^ (_reg >> 4)) & 0x1;

    /// <summary>PRBS_SHIFT.</summary>
    public void Shift() => _reg = (((_reg ^ (_reg >> 4)) & 0x1) << 8) | (_reg >> 1);
}
