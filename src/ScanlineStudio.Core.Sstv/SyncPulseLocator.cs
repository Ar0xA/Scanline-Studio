using System.Buffers;

namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// Finds where a constant-frequency sync pulse starts inside a span of raw samples: slides a window of
/// the pulse's full length one sample at a time and scores each position by how much of the window's
/// energy a single least-squares sinusoid explains (fitted energy / window energy, 1.0 = pure tone).
/// A full-length window peaks at exactly one position; a shorter one would score a plateau.
/// <para>Every position's 2×2 fit comes from prefix sums over the span, so a search costs
/// O(span × frequencies), not O(span × pulse length). Pure function; measurement only.</para>
/// </summary>
internal static class SyncPulseLocator
{
    /// <summary>Best start (index into the span), its score, the frequency that scored it, and whether
    /// it sits on the first or last searched position (the true start may lie outside the range).</summary>
    internal readonly record struct Location(int Start, double Score, double FrequencyHz, bool AtEdge)
    {
        public static readonly Location None = new(-1, double.NaN, double.NaN, false);

        public bool IsValid => Start >= 0 && double.IsFinite(Score);
    }

    /// <param name="span">Raw samples covering every candidate window: at least positions − 1 + pulseLength.</param>
    /// <param name="positions">Candidate starts 0 … positions − 1.</param>
    /// <param name="pulseLength">Window length in samples.</param>
    /// <param name="sampleRate">Rate of the raw stream.</param>
    /// <param name="frequenciesHz">Tone frequencies to try; the best over all of them wins.</param>
    internal static Location Locate(ReadOnlySpan<float> span, int positions, int pulseLength, int sampleRate, ReadOnlySpan<double> frequenciesHz)
    {
        if (positions < 1 || pulseLength < 4 || span.Length < positions - 1 + pulseLength || frequenciesHz.IsEmpty)
        {
            return Location.None;
        }

        var length = positions - 1 + pulseLength;
        var pool = ArrayPool<double>.Shared;
        var pxx = pool.Rent(length + 1);
        var pcc = pool.Rent(length + 1);
        var pss = pool.Rent(length + 1);
        var pcs = pool.Rent(length + 1);
        var pxc = pool.Rent(length + 1);
        var pxs = pool.Rent(length + 1);
        try
        {
            pxx[0] = 0;
            for (var i = 0; i < length; i++)
            {
                double x = span[i];
                pxx[i + 1] = pxx[i] + (x * x);
            }

            var best = Location.None;
            foreach (var frequencyHz in frequenciesHz)
            {
                var omega = 2.0 * Math.PI * frequencyHz / sampleRate;
                var (stepCos, stepSin) = (Math.Cos(omega), Math.Sin(omega));
                double c = 1, s = 0;
                pcc[0] = pss[0] = pcs[0] = pxc[0] = pxs[0] = 0;
                for (var i = 0; i < length; i++)
                {
                    double x = span[i];
                    pcc[i + 1] = pcc[i] + (c * c);
                    pss[i + 1] = pss[i] + (s * s);
                    pcs[i + 1] = pcs[i] + (c * s);
                    pxc[i + 1] = pxc[i] + (x * c);
                    pxs[i + 1] = pxs[i] + (x * s);
                    (c, s) = ((c * stepCos) - (s * stepSin), (s * stepCos) + (c * stepSin));
                }

                for (var p = 0; p < positions; p++)
                {
                    var e = p + pulseLength;
                    var sxx = pxx[e] - pxx[p];
                    var scc = pcc[e] - pcc[p];
                    var sss = pss[e] - pss[p];
                    var scs = pcs[e] - pcs[p];
                    var sxc = pxc[e] - pxc[p];
                    var sxs = pxs[e] - pxs[p];
                    var det = (scc * sss) - (scs * scs);
                    if (!(det > 1e-9 * scc * sss) || !(sxx > 0))
                    {
                        continue;
                    }

                    var a = ((sxc * sss) - (sxs * scs)) / det;
                    var b = ((sxs * scc) - (sxc * scs)) / det;
                    var score = ((a * sxc) + (b * sxs)) / sxx;
                    if (!best.IsValid || score > best.Score)
                    {
                        best = new Location(p, score, frequencyHz, positions > 1 && (p == 0 || p == positions - 1));
                    }
                }
            }

            return best;
        }
        finally
        {
            pool.Return(pxx);
            pool.Return(pcc);
            pool.Return(pss);
            pool.Return(pcs);
            pool.Return(pxc);
            pool.Return(pxs);
        }
    }
}
