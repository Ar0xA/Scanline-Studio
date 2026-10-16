using System.Numerics;

namespace ScanlineStudio.Core.Drm.Tx;

/// <summary>The real-valued output of CTransmitData (drmtx/common/DRMSignalIO.cpp:40-96, OF_REAL_VAL):
/// real part × rNormFactor, cast to short.</summary>
internal static class DrmTxSampleConverter
{
    /// <summary>rNormFactor, overwritten to 1000 on every call (DRMSignalIO.cpp:44).</summary>
    public const double NormFactor = 1000.0;

    /// <summary>x86-64 <c>(short)(double)</c>: cvttsd2si truncates toward zero to int32, then the low
    /// 16 bits are kept, so out-of-range values wrap rather than saturate.</summary>
    public static short ToInt16(double value) => unchecked((short)TruncateToInt32(value));

    /// <summary>One symbol's samples: (short)(re · 1000) (DRMSignalIO.cpp:50).</summary>
    public static void ToInt16(ReadOnlySpan<Complex> samples, Span<short> output)
    {
        if (output.Length < samples.Length)
            throw new ArgumentException("Output is too short.", nameof(output));
        for (int i = 0; i < samples.Length; i++)
            output[i] = ToInt16(samples[i].Real * NormFactor);
    }

    /// <summary>int16 to float for playback, /32768.</summary>
    public static void ToFloat(ReadOnlySpan<short> samples, Span<float> output)
    {
        if (output.Length < samples.Length)
            throw new ArgumentException("Output is too short.", nameof(output));
        for (int i = 0; i < samples.Length; i++)
            output[i] = samples[i] / 32768f;
    }

    // cvttsd2si returns 0x80000000 for NaN and out-of-range input; .NET's own conversion is platform-defined there.
    private static int TruncateToInt32(double value) =>
        double.IsNaN(value) || value >= 2147483648.0 || value <= -2147483649.0 ? int.MinValue : (int)value;
}
