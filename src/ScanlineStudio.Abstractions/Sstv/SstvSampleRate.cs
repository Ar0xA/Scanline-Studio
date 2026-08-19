namespace ScanlineStudio.Abstractions.Sstv;

/// <summary>Configured SSTV DSP sample-rate policy. Legacy stores this value as a
/// <c>double</c> and accepts 5000.0 through 48500.0 inclusive (<c>CLOCKMAX</c>,
/// <c>ComLib.h:53</c>; <c>Main.cpp:1635-1641</c>). Scanline Studio's audio and DSP
/// APIs already use whole-Hz <see cref="int"/> values, so fractional legacy rates remain an
/// explicit compatibility limitation rather than being silently rounded here.</summary>
public static class SstvSampleRate
{
    public const int Default = 11025;
    public const int Minimum = 5000;
    public const int Maximum = 48500;

    public static bool IsSupported(int sampleRate) => sampleRate is >= Minimum and <= Maximum;

    /// <summary>Matches legacy startup's invalid persisted-value behavior: fall back to 11025.
    /// Options-save behavior is intentionally different—legacy preserves the prior valid value
    /// there—and is implemented at that settings boundary.</summary>
    public static int NormalizePersisted(int sampleRate) => IsSupported(sampleRate) ? sampleRate : Default;

    /// <summary>Largest rate Auto Slant can commit for a configured nominal rate. The operation
    /// order deliberately mirrors <c>Main.cpp:4012-4015</c> followed by
    /// <c>NormalSampFreq(value, 50)</c> (<c>ComLib.cpp:203-207</c>), including its 0.02-Hz
    /// rounding step.</summary>
    public static double MaximumAutoSlantRate(int sampleRate)
    {
        var rawClamp = (double)sampleRate * 1100d / 1060d;
        return Math.Floor(rawClamp * 50d + 0.5d) / 50d;
    }
}
