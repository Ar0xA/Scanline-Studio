using System.Numerics;

namespace ScanlineStudio.Core.Drm.Tx;

/// <summary>Standard-mapping QAM, port of CQAMMapping::Map (drmtx/common/mlc/QAMMapping.cpp:33-116) with
/// the tables of tables/TableQAMMapping.h. QSSTV writes the table values as float literals
/// ("0.7071067811f") into double, so each value here is the float rounding widened to double.</summary>
internal static class QamMapper
{
    private static readonly double[] Qam4 = [0.7071067811f, -0.7071067811f];

    private static readonly double[] Qam16 = [0.9486832980f, -0.3162277660f, 0.3162277660f, -0.9486832980f];

    private static readonly double[] Qam64 =
        [1.0801234497f, -0.1543033499f, 0.4629100498f, -0.7715167498f, 0.7715167498f, -0.4629100498f, 0.1543033499f, -1.0801234497f];

    /// <summary>Maps the interleaved level outputs (each 2·cells bits) onto <paramref name="output"/>.
    /// Even bits give the real part, odd bits the imaginary part; level 0 is the most significant.</summary>
    public static void Map(DrmQam qam, IReadOnlyList<byte[]> levels, Span<Complex> output)
    {
        ArgumentNullException.ThrowIfNull(levels);
        switch (qam)
        {
            case DrmQam.Qam4:
            {
                byte[] y0 = levels[0];
                for (int i = 0; i < output.Length; i++)
                    output[i] = new Complex(Qam4[y0[2 * i] & 1], Qam4[y0[(2 * i) + 1] & 1]);
                break;
            }

            case DrmQam.Qam16:
            {
                byte[] y0 = levels[0], y1 = levels[1];
                for (int i = 0; i < output.Length; i++)
                {
                    int re = ((y0[2 * i] & 1) << 1) | (y1[2 * i] & 1);
                    int im = ((y0[(2 * i) + 1] & 1) << 1) | (y1[(2 * i) + 1] & 1);
                    output[i] = new Complex(Qam16[re], Qam16[im]);
                }

                break;
            }

            case DrmQam.Qam64:
            {
                byte[] y0 = levels[0], y1 = levels[1], y2 = levels[2];
                for (int i = 0; i < output.Length; i++)
                {
                    int re = ((y0[2 * i] & 1) << 2) | ((y1[2 * i] & 1) << 1) | (y2[2 * i] & 1);
                    int im = ((y0[(2 * i) + 1] & 1) << 2) | ((y1[(2 * i) + 1] & 1) << 1) | (y2[(2 * i) + 1] & 1);
                    output[i] = new Complex(Qam64[re], Qam64[im]);
                }

                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(qam), qam, null);
        }
    }
}
