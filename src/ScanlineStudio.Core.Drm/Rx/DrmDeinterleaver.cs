using System.Numerics;

namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>QSSTV RX's bit/cell deinterleaver table, <c>deinterleaver</c> (drmrx/deinterleaver.cpp:33-125; M.Bos
/// PA0MBO after Schorr's deinterleaver.m, "GNU General Public License", no version stated): two concatenated
/// parts A and B, each the DRM pseudo-random permutation Π(i) = (t·Π(i−1) + q) mod s with s the next power of two.</summary>
internal static class DrmDeinterleaver
{
    /// <summary><c>deinterleaver(xinA, tA, xinB, tB)</c>: writes <c>xinA + xinB</c> entries to <paramref name="table"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">QSSTV's own argument checks (deinterleaver.cpp:48-78), where it exits.</exception>
    public static void Build(int xinA, int tA, int xinB, int tB, Span<int> table)
    {
        if (xinA < 0 || tA < 1 || xinB < 6 || tB < 1)
            throw new ArgumentOutOfRangeException(nameof(xinB), "deinterleaver: xinA >= 0, xinB >= 6, tA/tB >= 1.");
        if (table.Length < xinA + xinB)
            throw new ArgumentException("Table too short.", nameof(table));

        int sA = PowerOfTwoAbove(xinA);
        int qA = sA / 4 - 1;
        int sB = PowerOfTwoAbove(xinB);
        int qB = sB / 4 - 1;
        table[0] = 0;
        int pi = 0;
        for (int i = 1; i <= xinA - 1; i++)
        {
            pi = (tA * pi + qA) % sA;
            while (pi >= xinA)
                pi = (tA * pi + qA) % sA;
            table[pi] = i;
        }

        table[xinA] = xinA;
        pi = 0;
        for (int i = 1; i <= xinB - 1; i++)
        {
            pi = (tB * pi + qB) % sB;
            while (pi >= xinB)
                pi = (tB * pi + qB) % sB;
            table[pi + xinA] = i + xinA;
        }
    }

    // deinterleaver.cpp:79-98: (int) pow(2, ceil(log(x) / log(2))); equal on every reachable x (tested).
    internal static int PowerOfTwoAbove(int x) => x == 0 ? 0 : (int)BitOperations.RoundUpToPowerOf2((uint)x);
}
