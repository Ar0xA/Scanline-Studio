namespace ScanlineStudio.Core.Drm.Util;

/// <summary>DRM block-interleaver permutation, port of CBlockInterleaver::MakeTable
/// (drmtx/common/interleaver/BlockInterleaver.cpp:35-69). Shared by the bit interleaver
/// (t0 = 13/21) and the symbol interleaver (t0 = 5).</summary>
internal static class BlockInterleaverTable
{
    /// <summary>Builds the permutation for <paramref name="frameSize"/> cells or bits.</summary>
    public static int[] Make(int frameSize, int t0)
    {
        if (frameSize is < 1 or >= 1 << 16)
            throw new ArgumentOutOfRangeException(nameof(frameSize), frameSize, "Frame size must be 1..65535.");

        // s = 2^(highest set bit + 1), found by shifting to bit 16 as QSSTV does: a power of two N gives s = 2N, not N.
        int highestOne = frameSize;
        int s = 1 << (16 + 1);
        while ((highestOne & (1 << 16)) == 0)
        {
            highestOne <<= 1;
            s >>= 1;
        }

        int q = (s / 4) - 1;
        var table = new int[frameSize];
        table[0] = 0;
        for (int i = 1; i < frameSize; i++)
        {
            table[i] = ((t0 * table[i - 1]) + q) % s;
            while (table[i] >= frameSize)
                table[i] = ((t0 * table[i]) + q) % s;
        }

        return table;
    }
}
