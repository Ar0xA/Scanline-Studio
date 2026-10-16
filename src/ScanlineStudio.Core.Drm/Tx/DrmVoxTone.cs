namespace ScanlineStudio.Core.Drm.Tx;

/// <summary>QSSTV's DRM VOX tone, <c>synthesPtr-&gt;sendTone(1., 1700., 0, false)</c> before the DRM frames
/// (txfunctions.cpp:207). The synthesizer looks samples up in a 2048-entry sine table of amplitude 8000,
/// advancing the phase before each lookup and rounding it to the nearest entry (synthes.cpp:37-56,63-75,
/// synthes.h:37-45). The sample is rounded to int16 (<c>(quint32)round(sample)</c>, synthes.cpp:132) and
/// played at sample/32768 like the DRM frames.</summary>
internal static class DrmVoxTone
{
    public const double DurationSeconds = 1.0;
    public const double FrequencyHz = 1700.0;

    private const int SinTableLength = 2048;
    private const double Amplitude = 8000.0;

    /// <summary>Samples in the tone at <paramref name="sampleRate"/>: <c>(unsigned)(duration·rate + 0.5)</c>,
    /// with <c>adjust</c> = 0 because <c>concat</c> is false.</summary>
    public static int SampleCount(int sampleRate) => (int)(uint)((DurationSeconds * sampleRate) + 0.5);

    /// <summary>The tone as int16, starting from phase 0. QSSTV's start phase depends on earlier tones, because
    /// all of them share the synthesizer's <c>oldAngle</c> (synthes.h:41-42); a whole 1 s VOX tone at 1700 Hz
    /// returns it to 0. The start phase of a VOX trigger tone is not wire-relevant.</summary>
    public static short[] GenerateInt16(int sampleRate)
    {
        var table = new double[SinTableLength];
        for (int i = 0; i < SinTableLength; i++)
            table[i] = Math.Sin(((double)i * Math.PI * 2.0) / SinTableLength) * Amplitude;

        var result = new short[SampleCount(sampleRate)];
        double oldAngle = 0.0;
        for (int n = 0; n < result.Length; n++)
        {
            double temp = ((FrequencyHz / sampleRate) * SinTableLength) + oldAngle;

            // C fmod: the remainder takes the dividend's sign, as C#'s % on double does.
            oldAngle = temp % SinTableLength;
            int t = (int)(oldAngle + 0.5);
            double sample = table[t % SinTableLength];
            result[n] = unchecked((short)(long)Math.Round(sample, MidpointRounding.AwayFromZero));
        }

        return result;
    }

    /// <summary>The tone as playback floats, sample/32768.</summary>
    public static float[] Generate(int sampleRate)
    {
        short[] pcm = GenerateInt16(sampleRate);
        var result = new float[pcm.Length];
        DrmTxSampleConverter.ToFloat(pcm, result);
        return result;
    }
}
