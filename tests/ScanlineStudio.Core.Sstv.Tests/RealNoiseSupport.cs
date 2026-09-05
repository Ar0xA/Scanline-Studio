using System.Globalization;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>Design parameters of one FIR used by the real-HF-noise sweep, recorded verbatim in the
/// run report. Every SNR figure in both arms of the comparison is defined relative to a filter's
/// REAL response, not an ideal brick wall, so a later edit to the designer would silently redefine
/// every recorded number with no way to detect it after the fact.</summary>
public sealed record FirSpec(
    string Role,
    int Taps,
    double LowCutoffHz,
    double HighCutoffHz,
    int SampleRate,
    string Window)
{
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Role}: {Taps} taps, {LowCutoffHz:F0}-{HighCutoffHz:F0}Hz at {SampleRate}Hz, {Window} window");
}

/// <summary>Windowed-sinc FIR design and application for the real-HF-noise sweep.
///
/// Band power is measured as a time-domain RMS of the FIR-filtered signal, deliberately NOT as an
/// FFT band-power sum: that route has to get windowing, one-sided-vs-two-sided scaling, Parseval
/// normalization and bin-edge inclusion all right, and this project's <c>RadixTwoFft</c> would
/// additionally zero-pad a multi-minute buffer to the next power of two and silently change the N
/// being divided by. A fixed FIR plus <c>sqrt(mean(x^2))</c> has none of those failure modes.
///
/// Blackman window: about 74dB stopband attenuation, transition width about 5.5/taps in normalized
/// frequency. The stopband matters more than the transition here, because the whole point of the
/// measurement is that out-of-band noise power must not leak into an in-band figure.</summary>
public static class FirFilter
{
    // Blackman's own -- the constant only exists to keep the coefficient line readable.
    private const double BlackmanA0 = 0.42;
    private const double BlackmanA1 = 0.5;
    private const double BlackmanA2 = 0.08;

    public static double[] DesignLowPass(double cutoffHz, int sampleRate, int taps)
    {
        ValidateTaps(taps);
        if (cutoffHz <= 0 || cutoffHz >= sampleRate / 2.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cutoffHz), cutoffHz, $"Cutoff must be inside (0, {sampleRate / 2.0}) for a {sampleRate}Hz filter.");
        }

        var h = new double[taps];
        var mid = (taps - 1) / 2.0;
        var wc = 2.0 * Math.PI * cutoffHz / sampleRate;
        double sum = 0;
        for (var i = 0; i < taps; i++)
        {
            var n = i - mid;
            // sinc(0) is its limit, not a division -- n is exactly 0 only for the centre tap of an odd-length filter.
            var ideal = n == 0 ? wc / Math.PI : Math.Sin(wc * n) / (Math.PI * n);
            var w = BlackmanA0
                - (BlackmanA1 * Math.Cos(2.0 * Math.PI * i / (taps - 1)))
                + (BlackmanA2 * Math.Cos(4.0 * Math.PI * i / (taps - 1)));
            h[i] = ideal * w;
            sum += h[i];
        }

        // Normalize to unity DC gain so a filtered RMS is directly comparable to an unfiltered one.
        for (var i = 0; i < taps; i++)
        {
            h[i] /= sum;
        }

        return h;
    }

    /// <summary>Band-pass as the difference of two low-passes. Both share the same window and tap
    /// count, so their transition regions cancel cleanly at the subtraction.</summary>
    public static double[] DesignBandPass(double lowHz, double highHz, int sampleRate, int taps)
    {
        if (lowHz >= highHz)
        {
            throw new ArgumentOutOfRangeException(nameof(lowHz), lowHz, $"Low cutoff must be below high cutoff ({highHz}Hz).");
        }

        var high = DesignLowPass(highHz, sampleRate, taps);
        var low = DesignLowPass(lowHz, sampleRate, taps);
        var h = new double[taps];
        for (var i = 0; i < taps; i++)
        {
            h[i] = high[i] - low[i];
        }

        return h;
    }

    /// <summary>Filters <paramref name="samples"/> and returns the group-delay-aligned result, same
    /// length as the input. The filter's warm-up region is at the head, so callers that care about
    /// sample 0 (VIS lock does) must feed a longer buffer and trim, not rely on this alignment.</summary>
    public static float[] Apply(IReadOnlyList<float> samples, double[] coefficients)
    {
        var taps = coefficients.Length;
        var delay = (taps - 1) / 2;
        var output = new float[samples.Count];
        for (var i = 0; i < samples.Count; i++)
        {
            double acc = 0;
            var start = i + delay;
            for (var k = 0; k < taps; k++)
            {
                var idx = start - k;
                if (idx >= 0 && idx < samples.Count)
                {
                    acc += coefficients[k] * samples[idx];
                }
            }

            output[i] = (float)acc;
        }

        return output;
    }

    /// <summary>RMS of the signal after <paramref name="coefficients"/>, measured only over the
    /// region where the filter window is fully populated. Allocation-free relative to
    /// <see cref="Apply"/> -- the sweep calls this on multi-minute buffers.
    ///
    /// The edge exclusion is not cosmetic: a truncated convolution sees zeros beyond the buffer, and
    /// that step is broadband, so a stopband figure measured across the edges reports the edge
    /// transient rather than the filter. Excluding it costs one tap length out of a multi-minute
    /// stream and is what makes a stopband assertion mean the filter.</summary>
    public static double BandRms(IReadOnlyList<float> samples, double[] coefficients)
    {
        var taps = coefficients.Length;
        var delay = (taps - 1) / 2;
        var first = delay;
        var last = samples.Count - delay;
        if (last <= first)
        {
            throw new ArgumentException(
                $"Buffer of {samples.Count} samples is too short for a {taps}-tap filter; nothing remains after excluding both edges.",
                nameof(samples));
        }

        double sumSquares = 0;
        for (var i = first; i < last; i++)
        {
            double acc = 0;
            var start = i + delay;
            for (var k = 0; k < taps; k++)
            {
                var idx = start - k;
                if (idx >= 0 && idx < samples.Count)
                {
                    acc += coefficients[k] * samples[idx];
                }
            }

            sumSquares += acc * acc;
        }

        return Math.Sqrt(sumSquares / (last - first));
    }

    public static double Rms(IReadOnlyList<float> samples)
    {
        double sumSquares = 0;
        for (var i = 0; i < samples.Count; i++)
        {
            sumSquares += (double)samples[i] * samples[i];
        }

        return Math.Sqrt(sumSquares / samples.Count);
    }

    private static void ValidateTaps(int taps)
    {
        if (taps < 3 || taps % 2 == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(taps), taps, "Tap count must be odd and at least 3 (linear phase, integer group delay).");
        }
    }
}

/// <summary>Rational-ratio polyphase resampler, used only to bring the 48000Hz noise corpus to the
/// sweep's own 44100Hz. 48000 -> 44100 is exactly 147/160, so no rate approximation is involved.
///
/// Polyphase, not literal upsample-then-decimate: the x147 intermediate for a 290s stream would be
/// 2.05e9 samples, about 8GB as float32. Only the output samples that survive decimation are ever
/// computed.</summary>
public static class PolyphaseResampler
{
    public const int UpFactor = 147;
    public const int DownFactor = 160;

    // Per-phase tap count. 32 gives a prototype of 4704 taps at the 7.056MHz intermediate rate,
    // whose transition band is far narrower than the 3.5kHz-limited corpus needs.
    private const int TapsPerPhase = 32;

    public static FirSpec Spec(int inputSampleRate) => new(
        Role: "resampler-prototype",
        Taps: (UpFactor * TapsPerPhase) + 1,
        LowCutoffHz: 0,
        HighCutoffHz: CutoffHz(inputSampleRate),
        SampleRate: inputSampleRate * UpFactor,
        Window: "blackman");

    /// <summary>Group delay in OUTPUT samples. The prototype's warm-up lands at output sample 0,
    /// which is exactly where VIS lock happens, so the sweep resamples a longer stream and drops
    /// this many samples from the head.</summary>
    public static int OutputGroupDelay => (((UpFactor * TapsPerPhase) + 1) - 1) / 2 / DownFactor;

    public static float[] Resample(IReadOnlyList<float> input, int inputSampleRate)
    {
        var prototypeTaps = (UpFactor * TapsPerPhase) + 1;
        var intermediateRate = inputSampleRate * UpFactor;
        var h = FirFilter.DesignLowPass(CutoffHz(inputSampleRate), intermediateRate, prototypeTaps);

        var outputCount = (int)((long)input.Count * UpFactor / DownFactor);
        var output = new float[outputCount];
        for (var m = 0; m < outputCount; m++)
        {
            var t = (long)m * DownFactor;
            var phase = (int)(t % UpFactor);
            var baseIndex = (int)(t / UpFactor);

            double acc = 0;
            for (var j = 0; j < TapsPerPhase; j++)
            {
                var k = phase + (j * UpFactor);
                if (k >= prototypeTaps)
                {
                    break;
                }

                var idx = baseIndex - j;
                if (idx >= 0 && idx < input.Count)
                {
                    acc += h[k] * input[idx];
                }
            }

            // Zero-stuffing divides the signal's power across UpFactor slots; this restores the level.
            output[m] = (float)(acc * UpFactor);
        }

        return output;
    }

    // Below both Nyquists, with margin. The corpus stops near 3.5kHz, so this cutoff is never the
    // limiting factor on content -- it only has to suppress the zero-stuffing images.
    private static double CutoffHz(int inputSampleRate) =>
        Math.Min(inputSampleRate, inputSampleRate * (double)UpFactor / DownFactor) / 2.0 * 0.9;
}
