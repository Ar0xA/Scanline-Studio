namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>QSSTV RX's bit packing, <c>bits2bytes</c> (drmrx/bits2bytes.cpp:56-76; Schorr, Univ. Kaiserslautern 2004,
/// GPL-2.0-or-later, adapted by M.Bos PA0MBO): MSB first, a bit is 1 when |v| &gt; DBL_EPSILON.</summary>
internal static class DrmRxBits
{
    private const double DblEpsilon = 2.220446049250313E-16;

    /// <summary><c>bits2bytes(inbits, N, outbytes)</c>; like QSSTV it writes nothing when N is not a multiple of 8.</summary>
    public static void BitsToBytes(ReadOnlySpan<double> inbits, Span<byte> outbytes)
    {
        int n = inbits.Length;
        if (n % 8 != 0)
            return;
        for (int m = 0; m < n / 8; m++)
        {
            int b = 0;
            for (int k = 7; k >= 0; k--)
                b |= (Math.Abs(inbits[8 * m + 7 - k]) > DblEpsilon ? 1 : 0) << k;
            outbytes[m] = (byte)b;
        }
    }
}
