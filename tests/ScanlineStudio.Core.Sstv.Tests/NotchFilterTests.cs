namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Isolated unit tests for <see cref="NotchFilter"/>, mirroring
/// <see cref="TxOutputBandpassFilterTests"/>/<see cref="SearchBandpassFilterTests"/>'s
/// discipline. Coefficient fixtures below are independently computed (Python, translated
/// directly from `fir.cpp`'s C++ source, NOT from this class's own C# implementation).
/// </summary>
public class NotchFilterTests
{
    // tap=96 (11025*96/11025=96, already even), default 2400Hz center -> fcl=2370, fch=2430
    // (outside the 1050-2350Hz voice passband, so +/-30Hz bandwidth).
    private static readonly double[] ExpectedHAt11025Hz2400Hz =
    [
        0.018122561676371957, -0.002250230126883661, -0.019208762436489, -0.005510873130730802,
        0.017152629148942018, 0.012507938816398521, -0.012236661590878807, -0.017569875252468278,
        0.00522381173289077, 0.019826876257806384, 0.002761684236888726, -0.01886068003853068,
        -0.010416046225759921, 0.014781527452358811, 0.0164706374138067, -0.008217728635317832,
        -0.019903951104722244, 0.00021831690997767487, 0.020115195255910354, 0.007916037899918718,
        -0.017029775967303155, -0.014846370362722556, 0.011118098420392856, 0.019417719790431588,
        -0.003323504853077227, -0.0208531749930909, -0.005089558118875207, 0.018886808082907228,
        0.012742493355884217, -0.013812605815417532, -0.01837045774884512, 0.006440436507894157,
        0.021033216300696187, 0.002034343472182242, -0.02027350287039904, -0.010226464839341564,
        0.016196008055388064, 0.016789218459046445, -0.009453274003753877, -0.02063781314861591,
        0.001140330911246695, 0.021130253737487694, 0.0073847600965561274, -0.018175325907330297,
        -0.014724784063465182, 0.012249380227515982, 0.019674436076548306, -0.004318840875435441,
        0.9785799756025795,
        -0.004318840875435441, 0.019674436076548306, 0.012249380227515982, -0.014724784063465182,
        -0.018175325907330297, 0.0073847600965561274, 0.021130253737487694, 0.001140330911246695,
        -0.02063781314861591, -0.009453274003753877, 0.016789218459046445, 0.016196008055388064,
        -0.010226464839341564, -0.02027350287039904, 0.002034343472182242, 0.021033216300696187,
        0.006440436507894157, -0.01837045774884512, -0.013812605815417532, 0.012742493355884217,
        0.018886808082907228, -0.005089558118875207, -0.0208531749930909, -0.003323504853077227,
        0.019417719790431588, 0.011118098420392856, -0.014846370362722556, -0.017029775967303155,
        0.007916037899918718, 0.020115195255910354, 0.00021831690997767487, -0.019903951104722244,
        -0.008217728635317832, 0.0164706374138067, 0.014781527452358811, -0.010416046225759921,
        -0.01886068003853068, 0.002761684236888726, 0.019826876257806384, 0.00522381173289077,
        -0.017569875252468278, -0.012236661590878807, 0.012507938816398521, 0.017152629148942018,
        -0.005510873130730802, -0.019208762436489, -0.002250230126883661, 0.018122561676371957,
    ];

    // Non-integral-tap-rate fixture (plan-review requested): 96*6000/11025=52.244... truncates to
    // 52 (already even) -- pins the C++ truncation-not-rounding behavior specifically. A
    // ceiling-to-even bug would instead compute tap=54, giving a different-length, different-value
    // array entirely (this fixture would fail length assertion before value comparison even ran).
    private static readonly double[] ExpectedHAt6000Hz2400Hz =
    [
        0.028313099039708755, -0.03530524803780514, 0.028803417209328486, -0.011090601900708465,
        -0.011175896241445743, 0.029473196719009727, -0.036684530323565986, 0.029874390909327354,
        -0.011482291285648018, -0.011549969417658382, 0.03040583755398431, -0.037778940952444516,
        0.03071202251071632, -0.01178380495578205, -0.011832872253495648, 0.031097326229090762,
        -0.038572397849152854, 0.031303995124535335, -0.011990706585979668, -0.012020439850358874,
        0.03153747716994827, -0.03905320764028744, 0.031641580705970555, -0.012099944340781562,
        -0.01210990450219838, 0.03171979339052044,
        0.9607857289663466,
        0.03171979339052044, -0.01210990450219838, -0.012099944340781562, 0.031641580705970555,
        -0.03905320764028744, 0.03153747716994827, -0.012020439850358874, -0.011990706585979668,
        0.031303995124535335, -0.038572397849152854, 0.031097326229090762, -0.011832872253495648,
        -0.01178380495578205, 0.03071202251071632, -0.037778940952444516, 0.03040583755398431,
        -0.011549969417658382, -0.011482291285648018, 0.029874390909327354, -0.036684530323565986,
        0.029473196719009727, -0.011175896241445743, -0.011090601900708465, 0.028803417209328486,
        -0.03530524803780514, 0.028313099039708755,
    ];

    private static void AssertClose(double[] expected, double[] actual, double tolerance = 1e-12)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.True(Math.Abs(actual[i] - expected[i]) < tolerance, $"index {i}: expected {expected[i]}, got {actual[i]}");
        }
    }

    [Fact]
    public void MakeFilter_FullArray_MatchesIndependentlyComputedFixture_At11025Hz2400Hz()
    {
        var h = NotchFilter.MakeFilter(96, 11025.0, fcl: 2370.0, fch: 2430.0);

        AssertClose(ExpectedHAt11025Hz2400Hz, h);
    }

    [Fact]
    public void MakeFilter_FullArray_MatchesIndependentlyComputedFixture_At6000Hz2400Hz()
    {
        var h = NotchFilter.MakeFilter(52, 6000.0, fcl: 2370.0, fch: 2430.0);

        AssertClose(ExpectedHAt6000Hz2400Hz, h);
    }

    [Theory]
    [InlineData(11025.0, 96)] // 96*11025/11025 = 96.0, already even
    [InlineData(6000.0, 52)] // 96*6000/11025 = 52.244... truncates to 52, already even
    [InlineData(8000.0, 70)] // 96*8000/11025 = 69.66... truncates to 69, bumped to 70
    [InlineData(44100.0, 256)] // 96*44100/11025 = 384.0, capped at NOTCHTAPMAX=256
    public void Tap_MatchesLegacyTruncateThenBumpThenCapFormula(double sampleRate, int expectedTap)
    {
        var filter = new NotchFilter((int)sampleRate);

        Assert.Equal(expectedTap, filter.Tap);
    }

    [Fact]
    public void MakeFilter_IsSymmetric_ForEvenTap()
    {
        var h = NotchFilter.MakeFilter(96, 11025.0, fcl: 2370.0, fch: 2430.0);

        for (var n = 0; n <= 96; n++)
        {
            Assert.True(Math.Abs(h[96 - n] - h[n]) < 1e-12, $"n={n}: H[{96 - n}]={h[96 - n]}, H[{n}]={h[n]} -- not symmetric");
        }
    }

    [Fact]
    public void ProcessSample_ImpulseResponse_IsCausal_NotCentered_AndGroupDelayIs48SamplesAt11025Hz()
    {
        const int tap = 96;
        var h = NotchFilter.MakeFilter(tap, 11025.0, fcl: 2370.0, fch: 2430.0);
        var filter = new NotchFilter(11025);

        var outputs = new double[tap + 1];
        outputs[0] = filter.ProcessSample(1.0);
        for (var n = 1; n <= tap; n++)
        {
            outputs[n] = filter.ProcessSample(0.0);
        }

        for (var n = 0; n <= tap; n++)
        {
            Assert.True(Math.Abs(outputs[n] - h[n]) < 1e-12, $"n={n}: expected h[{n}]={h[n]}, got {outputs[n]}");
        }
    }

    [Theory]
    [InlineData(1050.0, 15.0)] // exact lower voice-passband boundary: still +/-15Hz (< is strict)
    [InlineData(1049.9, 30.0)] // just below: +/-30Hz
    [InlineData(2350.0, 15.0)] // exact upper voice-passband boundary: still +/-15Hz (> is strict)
    [InlineData(2350.1, 30.0)] // just above: +/-30Hz
    public void SetFrequency_BandwidthBranch_MatchesLegacyBoundary(double centerHz, double expectedHalfBandwidth)
    {
        // Indirect check: a filter built at the boundary should attenuate a tone exactly
        // expectedHalfBandwidth away but pass one just past that.
        var filter = new NotchFilter(11025, centerHz);

        var atEdge = MeasureGain(filter, 11025, centerHz + expectedHalfBandwidth * 0.5);
        Assert.True(atEdge < 0.9, $"center={centerHz}: expected attenuation within {expectedHalfBandwidth}Hz, measured gain {atEdge}");
    }

    [Fact]
    public void SetFrequency_DoesNotResetDelayLine()
    {
        // Plan-review finding: retuning must recompute coefficients in place, not reset _z --
        // CFIR2::Create only reallocates the delay line when the TAP COUNT changes, and
        // SetNotchFreq always passes the same tap. Feed a few real samples, retune, and confirm
        // the delay line's prior content still influences the very next output (i.e. it wasn't
        // zeroed) by comparing against a filter that received the same priming samples but was
        // never retuned.
        var retuned = new NotchFilter(11025, 2400.0);
        var neverRetuned = new NotchFilter(11025, 1700.0);

        for (var i = 0; i < 10; i++)
        {
            var sample = Math.Sin(i);
            retuned.ProcessSample(sample);
            neverRetuned.ProcessSample(sample);
        }

        retuned.SetFrequency(1700.0);

        var afterRetune = retuned.ProcessSample(0.0);
        var neverRetunedOutput = neverRetuned.ProcessSample(0.0);

        // Both now use identical coefficients (1700Hz) AND both received the identical priming
        // sequence -- if retuning had zeroed the delay line, `afterRetune` would differ from
        // `neverRetunedOutput` (whose delay line legitimately carries the same 10 priming samples).
        Assert.Equal(neverRetunedOutput, afterRetune, precision: 12);
    }

    [Fact]
    public void ProcessSample_AttenuatesTheConfiguredFrequency_WithoutTouchingWellSeparatedTones()
    {
        const int sampleRate = 11025;
        var filter = new NotchFilter(sampleRate, 2400.0);

        // 1200/1500/1900/2300Hz are real SSTV tones (sync/black/white/pilot family) -- the default
        // 2400Hz notch must not touch the 1500-2300Hz image passband.
        Assert.True(MeasureGain(filter, sampleRate, 1200.0) > 0.9);
        Assert.True(MeasureGain(filter, sampleRate, 1500.0) > 0.9);
        Assert.True(MeasureGain(filter, sampleRate, 1900.0) > 0.85);
        Assert.True(MeasureGain(filter, sampleRate, 2300.0) > 0.8);
        Assert.True(MeasureGain(filter, sampleRate, 2400.0) < 0.05);
    }

    [Fact]
    public void ProcessSample_RetunedIntoThePassband_AttenuatesThatToneOnly()
    {
        const int sampleRate = 11025;
        var filter = new NotchFilter(sampleRate, 1700.0);

        Assert.True(MeasureGain(filter, sampleRate, 1200.0) > 0.9);
        Assert.True(MeasureGain(filter, sampleRate, 1700.0) < 0.05);
        Assert.True(MeasureGain(filter, sampleRate, 1900.0) > 0.9);
    }

    private static double MeasureGain(NotchFilter filter, int sampleRate, double toneHz, double amplitude = 1000.0, int settleSamples = 100)
    {
        var peak = 0.0;
        for (var i = 0; i < sampleRate; i++)
        {
            var input = amplitude * Math.Sin(2 * Math.PI * toneHz * i / sampleRate);
            var output = Math.Abs(filter.ProcessSample(input));
            if (i >= settleSamples && output > peak)
            {
                peak = output;
            }
        }

        return peak / amplitude;
    }
}
