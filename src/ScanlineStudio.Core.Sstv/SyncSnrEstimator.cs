namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// Sync-pulse SNR estimator: a pure function of one window of raw samples taken from inside a line's
/// sync pulse. Least-squares fits a single sinusoid (cos + sin, full 2×2 normal equations — the two
/// are not orthogonal on a window of a few ms) at a refined frequency, and measures the noise as the
/// residual's periodogram summed over the 400–2500 Hz reference band (the H2 band the real-noise
/// sweep harness defines SNR in). No filtering, so no pre-roll and no group delay.
/// <para>Measurement only; it never touches decode state. No legacy precedent (legacy has no SNR
/// readout), nothing wire-observable.</para>
/// </summary>
internal static class SyncSnrEstimator
{
    internal const double BandLowHz = 400.0;
    internal const double BandHighHz = 2500.0;
    internal const double BandWidthHz = BandHighHz - BandLowHz;
    internal const double GridStepHz = 5.0;

    /// <summary>One window's fit. <see cref="IsValid"/> is false for a window too short to carry two
    /// in-band bins or for a degenerate fit; callers skip those.</summary>
    internal readonly record struct Estimate(double FittedHz, double TonePower, double NoisePower, bool AtGridEdge)
    {
        public static readonly Estimate Invalid = new(double.NaN, double.NaN, double.NaN, false);

        public bool IsValid => double.IsFinite(FittedHz) && double.IsFinite(TonePower) && double.IsFinite(NoisePower) && NoisePower > 1e-20;

        public double SnrDb => 10.0 * Math.Log10(TonePower / NoisePower);
    }

    /// <summary>Fits the tone within <paramref name="centreHz"/> ± <paramref name="halfSpanHz"/> (5 Hz
    /// grid, then parabolic refinement of the fitted power) and returns tone and in-band noise power
    /// in the same units (mean-square of the input).</summary>
    internal static Estimate Measure(ReadOnlySpan<float> window, int sampleRate, double centreHz, double halfSpanHz)
    {
        var n = window.Length;
        if (n < 8 || sampleRate <= 0)
        {
            return Estimate.Invalid;
        }

        var firstBin = (int)Math.Ceiling(BandLowHz * n / sampleRate);
        var lastBin = Math.Min((int)Math.Floor(BandHighHz * n / sampleRate), (n - 1) / 2);
        var bandBins = lastBin - firstBin + 1;
        if (bandBins < 2)
        {
            return Estimate.Invalid;
        }

        var steps = Math.Max(0, (int)Math.Round(halfSpanHz / GridStepHz));
        var bestIndex = 0;
        Span<double> energies = stackalloc double[(2 * steps) + 1];
        for (var k = 0; k < energies.Length; k++)
        {
            energies[k] = Fit(window, sampleRate, centreHz + ((k - steps) * GridStepHz)).Energy;
            if (energies[k] > energies[bestIndex])
            {
                bestIndex = k;
            }
        }

        var atEdge = steps > 0 && (bestIndex == 0 || bestIndex == energies.Length - 1);
        var fittedHz = centreHz + ((bestIndex - steps) * GridStepHz);
        if (bestIndex > 0 && bestIndex < energies.Length - 1)
        {
            var left = energies[bestIndex - 1];
            var mid = energies[bestIndex];
            var right = energies[bestIndex + 1];
            var curvature = left - (2 * mid) + right;
            if (curvature < 0)
            {
                fittedHz += Math.Clamp(0.5 * (left - right) / curvature, -1.0, 1.0) * GridStepHz;
            }
        }

        var fit = Fit(window, sampleRate, fittedHz);
        if (!double.IsFinite(fit.Energy))
        {
            return Estimate.Invalid;
        }

        // Residual periodogram over the in-band bins.
        var pooled = n > 1024 ? System.Buffers.ArrayPool<double>.Shared.Rent(n) : null;
        var residual = pooled is null ? stackalloc double[n] : pooled.AsSpan(0, n);
        var (toneCos, toneSin) = Rotator(fittedHz, sampleRate);
        double tc = 1, ts = 0;
        for (var i = 0; i < n; i++)
        {
            residual[i] = window[i] - ((fit.A * tc) + (fit.B * ts));
            (tc, ts) = ((tc * toneCos) - (ts * toneSin), (ts * toneCos) + (tc * toneSin));
        }

        var bandSum = 0.0;
        for (var bin = firstBin; bin <= lastBin; bin++)
        {
            var omega = 2.0 * Math.PI * bin / n;
            double re = 0, im = 0;
            double bc = 1, bs = 0;
            var (stepCos, stepSin) = (Math.Cos(omega), Math.Sin(omega));
            for (var i = 0; i < n; i++)
            {
                re += residual[i] * bc;
                im -= residual[i] * bs;
                (bc, bs) = ((bc * stepCos) - (bs * stepSin), (bs * stepCos) + (bc * stepSin));
            }

            bandSum += (re * re) + (im * im);
        }

        if (pooled is not null)
        {
            System.Buffers.ArrayPool<double>.Shared.Return(pooled);
        }

        // One-sided in-band power, + the 2 degrees of freedom the fit removed from inside the band,
        // normalised to exactly the 2100 Hz reference width.
        var noisePower = 2.0 * bandSum / ((double)n * n)
            * (bandBins / (bandBins - 1.0))
            * (BandWidthHz / (bandBins * (double)sampleRate / n));

        // The fit also captured the noise lying in its own 2-dimensional subspace (about one bin).
        var capturedNoise = noisePower * sampleRate / (BandWidthHz * n);
        var tonePower = (fit.Energy / n) - capturedNoise;

        return new Estimate(fittedHz, tonePower, noisePower, atEdge);
    }

    private static (double Cos, double Sin) Rotator(double frequencyHz, int sampleRate)
    {
        var omega = 2.0 * Math.PI * frequencyHz / sampleRate;
        return (Math.Cos(omega), Math.Sin(omega));
    }

    /// <summary>Least-squares a·cos + b·sin at <paramref name="frequencyHz"/>; Energy is the fitted
    /// sinusoid's sum of squares over the window.</summary>
    private static (double A, double B, double Energy) Fit(ReadOnlySpan<float> window, int sampleRate, double frequencyHz)
    {
        var (stepCos, stepSin) = Rotator(frequencyHz, sampleRate);
        double c = 1, s = 0;
        double scc = 0, sss = 0, scs = 0, sxc = 0, sxs = 0;
        for (var i = 0; i < window.Length; i++)
        {
            double x = window[i];
            scc += c * c;
            sss += s * s;
            scs += c * s;
            sxc += x * c;
            sxs += x * s;
            (c, s) = ((c * stepCos) - (s * stepSin), (s * stepCos) + (c * stepSin));
        }

        var det = (scc * sss) - (scs * scs);
        if (!(det > 1e-12 * scc * sss))
        {
            return (0, 0, double.NaN);
        }

        var a = ((sxc * sss) - (sxs * scs)) / det;
        var b = ((sxs * scc) - (sxc * scs)) / det;
        return (a, b, (a * sxc) + (b * sxs));
    }
}
