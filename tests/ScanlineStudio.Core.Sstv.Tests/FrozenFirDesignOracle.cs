namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Frozen copies of the three per-class FIR designers as they stood at commit <c>c79b76e7</c>, before
/// they were folded into one shared <c>FirDesign.MakeFilter</c>: <c>SearchBandpassFilter.MakeFilter</c>/<c>I0</c>,
/// <c>TxOutputBandpassFilter.MakeFilter</c>/<c>I0</c> (with the constants its body reads) and
/// <c>NotchFilter.MakeFilter</c>. Method bodies are verbatim; only the containing classes are renamed.
/// <see cref="FirDesignBitIdentityTests"/> compares production against these bit-for-bit, so never
/// edit a body here to make that test pass: a mismatch means production changed behaviour.
/// </summary>
internal static class FrozenFirDesignOracle
{
    /// <summary>Verbatim <c>SearchBandpassFilter.MakeFilter</c> and <c>I0</c> at <c>c79b76e7</c>.</summary>
    internal static class Search
    {
        internal static double[] MakeFilter(int tap, double sampleRate, double fcl, double fch, double att)
        {
            var half = tap / 2;
            var fc = (fch - fcl) / 2.0;
            var sumArg = 2.0 * Math.PI * fc / sampleRate;

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
                    prototype[j] = fc * 2.0 / sampleRate;
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

            var w0 = Math.PI * (fcl + fch) / sampleRate;
            for (var j = 0; j <= half; j++)
            {
                prototype[j] *= 2.0 * Math.Cos(j * w0);
            }

            var h = new double[tap + 1]; // zero-init -- matters for odd tap's untouched trailing slot
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

    /// <summary>Verbatim <c>TxOutputBandpassFilter.MakeFilter</c>, <c>I0</c> and the constants they
    /// read, at <c>c79b76e7</c>.</summary>
    internal static class Tx
    {
        private const double LowCutoffHz = 700.0;
        private const double HighCutoffHz = 2800.0;
        private const double AttenuationDb = 40.0;
        internal const int DefaultTapCount = 24;

        internal static double[] MakeFilter(double sampleRate, int tap = DefaultTapCount)
        {
            var half = tap / 2;
            var fc = (HighCutoffHz - LowCutoffHz) / 2.0;
            var sumArg = 2.0 * Math.PI * fc / sampleRate;

            // fir.cpp:365: alpha = 0.5842*pow(att-21,0.4) + 0.07886*(att-21), the 21<=att<50 branch.
            var alpha = 0.5842 * Math.Pow(AttenuationDb - 21.0, 0.4) + 0.07886 * (AttenuationDb - 21.0);
            var i0Alpha = I0(alpha);

            var prototype = new double[half + 1];
            for (var j = 0; j <= half; j++)
            {
                if (j == 0)
                {
                    prototype[j] = fc * 2.0 / sampleRate;
                    continue;
                }

                var fm = (2.0 * j) / tap;
                var win = I0(alpha * Math.Sqrt(1.0 - fm * fm)) / i0Alpha;
                prototype[j] = Math.Sin(j * sumArg) / (Math.PI * j) * win;
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

            var w0 = Math.PI * (LowCutoffHz + HighCutoffHz) / sampleRate;
            for (var j = 0; j <= half; j++)
            {
                prototype[j] *= 2.0 * Math.Cos(j * w0);
            }

            var h = new double[tap + 1];
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

        private static double I0(double x)
        {
            var sum = 1.0;
            var xj = 1.0;
            var j = 1;
            while (true)
            {
                xj *= (0.5 * x) / j;
                sum += xj * xj;
                j++;
                if (1e-8 * sum - xj * xj > 0)
                {
                    break;
                }
            }

            return sum;
        }
    }

    /// <summary>Verbatim <c>NotchFilter.MakeFilter</c> at <c>c79b76e7</c>.</summary>
    internal static class Notch
    {
        internal static double[] MakeFilter(int tap, double sampleRate, double fcl, double fch)
        {
            var half = tap / 2;
            var fc = (fch - fcl) / 2.0;
            var sumArg = 2.0 * Math.PI * fc / sampleRate;

            var prototype = new double[half + 1];
            for (var j = 0; j <= half; j++)
            {
                prototype[j] = j == 0
                    ? fc * 2.0 / sampleRate
                    : Math.Sin(j * sumArg) / (Math.PI * j);
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

            // BEF transform (fir.cpp:415-419) -- the DC-inverted, sign-flipped counterpart to ffBPF's
            // `*= +2*cos(j*w0)` applied to every tap including j=0 (SearchBandpassFilter.MakeFilter).
            var w0 = Math.PI * (fcl + fch) / sampleRate;
            prototype[0] = 1.0 - (2.0 * prototype[0]);
            for (var j = 1; j <= half; j++)
            {
                prototype[j] *= -2.0 * Math.Cos(j * w0);
            }

            var h = new double[tap + 1]; // zero-init -- matters for odd tap's untouched trailing slot
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
    }
}
