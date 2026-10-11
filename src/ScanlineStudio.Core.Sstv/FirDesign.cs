namespace ScanlineStudio.Core.Sstv;

/// <summary>Filter families <see cref="FirDesign.MakeFilter"/> builds: legacy's <c>ffBPF</c> and
/// <c>ffBEF</c> (`fir.h:35-36`).</summary>
internal enum FirKind
{
    Bpf,
    Bef,
}

/// <summary>
/// The single port of legacy's FIR designer <c>MakeFilter</c> (`fir.cpp:346-427`), shared by
/// <see cref="SearchBandpassFilter"/> (RX BPF), <see cref="TxOutputBandpassFilter"/> (TX BPF) and
/// <see cref="NotchFilter"/> (RX BEF).
///
/// Ported: the BPF/BEF prototype cutoff <c>fc=(fch-fcl)/2</c> (`fir.cpp:355-357`); the three-way
/// Kaiser <c>alpha</c> (`fir.cpp:361-369`); the windowed impulse response when <c>att&gt;=21</c>,
/// unwindowed otherwise (`fir.cpp:371-394`), with <c>j==0</c> never windowed; the DC normalisation
/// under its literal <c>sum&gt;0</c> guard (`fir.cpp:396-401`); the BPF/BEF frequency transform
/// (`fir.cpp:409-419`); and the two-loop mirror (`fir.cpp:421-426`), which writes
/// <c>2*(tap/2)+1</c> entries into a <c>tap+1</c> array. For an odd tap the last slot is never
/// written: legacy leaves it uninitialised (`fir.cpp:1102`), this port leaves it at zero.
///
/// Not ported: the <c>ffLPF</c>/<c>ffHPF</c> branches (`fir.cpp:352-354,358-360,405-408`), because no
/// caller builds those types, and the <c>gain</c> multiply in the mirror (`fir.cpp:422,425`), because
/// every caller passes 1.0.
///
/// Every tap for <c>j&gt;=1</c> is <c>sin(j*w)/(PI*j)</c> (times the window when windowed), where
/// legacy writes <c>(1.0/(PI*j))*sin(j*w)</c> (`fir.cpp:381` windowed, `fir.cpp:391` unwindowed);
/// the two can differ in the last ulp, and that difference against legacy is unmeasured.
/// </summary>
internal static class FirDesign
{
    /// <summary>Returns <c>tap+1</c> symmetric coefficients for a <paramref name="kind"/> filter
    /// between <paramref name="fcl"/> and <paramref name="fch"/> at sample rate <paramref name="fs"/>,
    /// Kaiser-windowed when <paramref name="att"/> is at least 21 dB.</summary>
    internal static double[] MakeFilter(FirKind kind, int tap, double fs, double fcl, double fch, double att)
    {
        var half = tap / 2;
        var fc = (fch - fcl) / 2.0;
        var sumArg = 2.0 * Math.PI * fc / fs;

        var useKaiser = att >= 21.0;
        var alpha = 0.0;
        var i0Alpha = 1.0;
        if (useKaiser)
        {
            alpha = att >= 50.0
                ? 0.1102 * (att - 8.7)
                : (0.5842 * Math.Pow(att - 21.0, 0.4)) + (0.07886 * (att - 21.0));
            i0Alpha = I0(alpha);
        }

        var prototype = new double[half + 1];
        for (var j = 0; j <= half; j++)
        {
            if (j == 0)
            {
                prototype[j] = fc * 2.0 / fs;
                continue;
            }

            prototype[j] = Math.Sin(j * sumArg) / (Math.PI * j);
            if (useKaiser)
            {
                var fm = 2.0 * j / tap;
                prototype[j] *= I0(alpha * Math.Sqrt(1.0 - (fm * fm))) / i0Alpha;
            }
        }

        var dcSum = prototype[0];
        for (var j = 1; j <= half; j++)
        {
            dcSum += 2.0 * prototype[j];
        }

        if (dcSum > 0.0)
        {
            for (var j = 0; j <= half; j++)
            {
                prototype[j] /= dcSum;
            }
        }

        var w0 = Math.PI * (fcl + fch) / fs;
        if (kind == FirKind.Bpf)
        {
            for (var j = 0; j <= half; j++)
            {
                prototype[j] *= 2.0 * Math.Cos(j * w0);
            }
        }
        else
        {
            prototype[0] = 1.0 - (2.0 * prototype[0]);
            for (var j = 1; j <= half; j++)
            {
                prototype[j] *= -2.0 * Math.Cos(j * w0);
            }
        }

        var h = new double[tap + 1]; // zero-init keeps the odd-tap trailing slot at 0.0
        var outIndex = 0;
        for (var j = half; j >= 0; j--)
        {
            h[outIndex++] = prototype[j];
        }

        for (var j = 1; j <= half; j++)
        {
            h[outIndex++] = prototype[j];
        }

        return h;
    }

    /// <summary>Port of legacy's truncated Bessel series <c>I0</c> (`fir.cpp:310-325`),
    /// <c>I0(x) = sum[((x/2)^k/k!)^2]</c>. It keeps legacy's add-then-check order: the term that first
    /// falls below the <c>1e-8*sum</c> threshold is still added before the loop exits.</summary>
    private static double I0(double x)
    {
        var sum = 1.0;
        var xj = 1.0;
        var j = 1;
        while (true)
        {
            xj *= 0.5 * x / j;
            sum += xj * xj;
            j++;
            if ((0.00000001 * sum) - (xj * xj) > 0)
            {
                break;
            }
        }

        return sum;
    }
}
