namespace ScanlineStudio.Core.Sstv;

/// <summary>Standard iterative in-place radix-2 Cooley-Tukey FFT. New code for the waterfall
/// visualization (<see cref="WaterfallSource"/>) — NOT a port of legacy `Fft.cpp`: CLAUDE.md's
/// "port first, invent second" rule is scoped to DSP/codec math that affects decoded-image
/// correctness (encode/decode, filters, demodulators); a waterfall display's exact FFT parameters
/// don't need legacy bit-parity, only to look like a useful spectrogram, so a standard textbook FFT
/// is the right call here, not a decompiled port.</summary>
internal static class RadixTwoFft
{
    /// <summary>In-place forward FFT. <paramref name="real"/>/<paramref name="imag"/> must be the
    /// same power-of-two length; <paramref name="imag"/> is typically all-zero on input (real-valued
    /// signal) and holds the imaginary output on return.</summary>
    public static void Forward(Span<float> real, Span<float> imag)
    {
        var n = real.Length;
        if (imag.Length != n)
        {
            throw new ArgumentException("real and imag must be the same length.", nameof(imag));
        }

        if (n == 0 || (n & (n - 1)) != 0)
        {
            throw new ArgumentException("Length must be a power of two.", nameof(real));
        }

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        // Iterative Cooley-Tukey butterflies.
        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = -2.0 * Math.PI / len;
            var wLenR = (float)Math.Cos(angle);
            var wLenI = (float)Math.Sin(angle);
            var half = len / 2;

            for (var i = 0; i < n; i += len)
            {
                float wR = 1f, wI = 0f;
                for (var j = 0; j < half; j++)
                {
                    var evenR = real[i + j];
                    var evenI = imag[i + j];
                    var oddR = real[i + j + half];
                    var oddI = imag[i + j + half];

                    var termR = oddR * wR - oddI * wI;
                    var termI = oddR * wI + oddI * wR;

                    real[i + j] = evenR + termR;
                    imag[i + j] = evenI + termI;
                    real[i + j + half] = evenR - termR;
                    imag[i + j + half] = evenI - termI;

                    var nextWR = (wR * wLenR) - (wI * wLenI);
                    var nextWI = (wR * wLenI) + (wI * wLenR);
                    wR = nextWR;
                    wI = nextWI;
                }
            }
        }
    }
}
