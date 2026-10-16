using ScanlineStudio.Core.Drm.Tables;

namespace ScanlineStudio.Core.Drm.Tx;

/// <summary>MSC bits per 400 ms frame, <c>iNumDecodedBitsMSC</c>, as CMLC::CalculateParam computes it for
/// the MSC with equal error protection (drmtx/common/mlc/MLC.cpp:266-500):
/// sum over levels of RX_p * floor((2 * N_mux - 12) / RY_p).</summary>
internal static class MscCapacity
{
    public static int NumDecodedBits(CellMappingTable table, DrmQam qam, DrmProtection protection)
    {
        ArgumentNullException.ThrowIfNull(table);
        int nMux = table.NumUsefulMscCellsPerFrame;
        int partB = protection == DrmProtection.Low ? 1 : 0;
        switch (qam)
        {
            case DrmQam.Qam4:
                return LevelBits(MlcTables.CodeRateCombMsc4Sm, nMux);
            case DrmQam.Qam16:
            {
                int sum = 0;
                for (int i = 0; i < 2; i++)
                    sum += LevelBits(MlcTables.CodeRateCombMsc16Sm[(partB * 3) + i], nMux);
                return sum;
            }

            case DrmQam.Qam64:
            {
                int sum = 0;
                for (int i = 0; i < 3; i++)
                    sum += LevelBits(MlcTables.CodeRateCombMsc64Sm[(partB * 4) + i], nMux);
                return sum;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(qam), qam, null);
        }
    }

    private static int LevelBits(int codeRate, int nMux)
    {
        int rx = MlcTables.PuncturingPatterns[codeRate * MlcTables.PuncturingPatternRowLength];
        int ry = MlcTables.PuncturingPatterns[(codeRate * MlcTables.PuncturingPatternRowLength) + 1];
        return rx * (((2 * nMux) - 12) / ry);
    }
}
