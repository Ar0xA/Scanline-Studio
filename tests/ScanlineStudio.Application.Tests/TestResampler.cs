namespace ScanlineStudio.Application.Tests;

internal static class TestResampler
{
    // Blackman-windowed sinc, cut off at 45 % of the output rate: enough for a test signal band-limited to 2.8 kHz.
    public static float[] Resample(float[] input, int inRate, int outRate)
    {
        const int Half = 128;
        double ratio = (double)outRate / inRate;
        double fc = 0.45 * Math.Min(1.0, ratio);
        var output = new float[(int)(input.Length * ratio)];
        for (int n = 0; n < output.Length; n++)
        {
            double t = n / ratio;
            int center = (int)Math.Floor(t);
            double acc = 0;
            for (int k = center - Half + 1; k <= center + Half; k++)
            {
                if ((uint)k >= (uint)input.Length)
                    continue;
                double x = t - k;
                double sinc = x == 0 ? 1 : Math.Sin(2 * Math.PI * fc * x) / (Math.PI * x);
                double w = 0.42 + (0.5 * Math.Cos(Math.PI * x / Half)) + (0.08 * Math.Cos(2 * Math.PI * x / Half));
                acc += input[k] * (x == 0 ? 2 * fc : sinc) * w;
            }

            output[n] = (float)acc;
        }

        return output;
    }
}
