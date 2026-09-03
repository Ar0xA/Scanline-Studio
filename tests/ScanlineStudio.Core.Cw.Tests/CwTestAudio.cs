using ScanlineStudio.Core.Sstv;

namespace ScanlineStudio.Core.Cw.Tests;

/// <summary>Renders <see cref="CwMorseGenerator"/>'s real (FrequencyHz, DurationMs) tone-segment
/// output into actual PCM float samples, so <see cref="ClassicalCwDecoder"/> tests exercise the
/// SAME already-verified TX-side generator real production code uses, not a second, independently
/// written (and independently buggy-in-its-own-way) test-only encoder.</summary>
internal static class CwTestAudio
{
    public static float[] Render(IEnumerable<(double FrequencyHz, double DurationMs)> segments, int sampleRate)
    {
        var samples = new List<float>();
        var phase = 0.0;
        foreach (var (frequencyHz, durationMs) in segments)
        {
            var sampleCount = (int)(durationMs * sampleRate / 1000.0);
            for (var i = 0; i < sampleCount; i++)
            {
                if (frequencyHz > 0)
                {
                    samples.Add((float)Math.Sin(phase));
                    phase += 2.0 * Math.PI * frequencyHz / sampleRate;
                    if (phase > 2.0 * Math.PI)
                    {
                        phase -= 2.0 * Math.PI;
                    }
                }
                else
                {
                    samples.Add(0f);
                    phase = 0;
                }
            }
        }

        return samples.ToArray();
    }

    /// <summary>Generates real CW-ID audio for <paramref name="text"/> at <paramref name="wpm"/>,
    /// using this port's OWN dot-duration convention (<see cref="CwMorseGenerator.MillisecondsPerDotFromWpm"/>,
    /// 1110/WPM -- not legacy's truncating formula), matching what this port's own encoder would
    /// actually transmit.</summary>
    public static float[] GenerateCwId(string text, double toneHz, double wpm, int sampleRate)
    {
        var dotMs = CwMorseGenerator.MillisecondsPerDotFromWpm(wpm);
        return Render(CwMorseGenerator.Generate(text, toneHz, dotMs), sampleRate);
    }

    public static float[] Concat(params float[][] chunks) => chunks.SelectMany(c => c).ToArray();

    public static float[] Silence(double durationMs, int sampleRate) => new float[(int)(durationMs * sampleRate / 1000.0)];

    /// <summary>Additive white Gaussian noise via Box-Muller, seeded for reproducibility (a flaky
    /// DSP test that only fails occasionally is worse than no test).</summary>
    public static float[] AddNoise(float[] samples, double snrDb, int seed)
    {
        var random = new Random(seed);
        var signalPower = samples.Length > 0 ? samples.Select(s => (double)s * s).Average() : 0.0;
        if (signalPower <= 0)
        {
            signalPower = 0.5; // full-scale sine RMS^2 -- a silent input still gets a real noise floor.
        }

        var noisePower = signalPower / Math.Pow(10, snrDb / 10.0);
        var noiseStdDev = Math.Sqrt(noisePower);

        var result = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            var u1 = 1.0 - random.NextDouble();
            var u2 = random.NextDouble();
            var gaussian = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            result[i] = samples[i] + (float)(gaussian * noiseStdDev);
        }

        return result;
    }
}
