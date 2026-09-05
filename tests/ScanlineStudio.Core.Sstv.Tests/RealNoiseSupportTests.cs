namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>Unit coverage for the real-HF-noise sweep's own DSP helpers. Every SNR figure the sweep
/// reports is defined by these two pieces, so they are tested in isolation before anything is wired
/// to a decoder.</summary>
public sealed class RealNoiseSupportTests
{
    private const int SweepSampleRate = 44100;
    private const int CorpusSampleRate = 48000;
    private const int MeasurementTaps = 1023;

    // H2 (search/pre-lock) governs whether a lock happens at all, and the sweep's pass/fail bar is a
    // lock test -- so this is the band the sweep calibrates on. SearchBandpassFilter.cs:125,138-140.
    private const double H2LowHz = 400;
    private const double H2HighHz = 2500;

    [Fact]
    public void DesignLowPass_PassesBelowCutoff_AndRejectsAbove()
    {
        var h = FirFilter.DesignLowPass(3500, SweepSampleRate, MeasurementTaps);

        var passed = FirFilter.BandRms(Tone(1000, SweepSampleRate, 1.0), h);
        var rejected = FirFilter.BandRms(Tone(8000, SweepSampleRate, 1.0), h);

        var toneRms = 1.0 / Math.Sqrt(2.0);
        Assert.InRange(passed, toneRms * 0.99, toneRms * 1.01);
        Assert.True(rejected < toneRms * 0.001, $"8kHz tone through a 3.5kHz low-pass: {rejected:E3}");
    }

    [Theory]
    [InlineData(1500, true)]   // inside H2
    [InlineData(2300, true)]   // inside H2, top of the SSTV video band
    [InlineData(200, false)]   // below H2's lower corner
    [InlineData(3500, false)]  // above H2's upper corner
    public void DesignBandPass_SelectsOnlyTheRequestedBand(double toneHz, bool expectPass)
    {
        var h = FirFilter.DesignBandPass(H2LowHz, H2HighHz, SweepSampleRate, MeasurementTaps);
        var toneRms = 1.0 / Math.Sqrt(2.0);

        var measured = FirFilter.BandRms(Tone(toneHz, SweepSampleRate, 1.0), h);

        if (expectPass)
        {
            Assert.InRange(measured, toneRms * 0.99, toneRms * 1.01);
        }
        else
        {
            Assert.True(measured < toneRms * 0.01, $"{toneHz}Hz tone through a {H2LowHz}-{H2HighHz}Hz band-pass: {measured:E3}");
        }
    }

    [Fact]
    public void BandRms_MatchesTheRmsOfTheFilteredSignal()
    {
        var h = FirFilter.DesignBandPass(H2LowHz, H2HighHz, SweepSampleRate, MeasurementTaps);
        var signal = Noise(SweepSampleRate, seed: 4242, seconds: 1.0);

        var viaBandRms = FirFilter.BandRms(signal, h);
        var delay = (MeasurementTaps - 1) / 2;
        var viaApply = FirFilter.Rms(FirFilter.Apply(signal, h)[delay..^delay]);

        // Same arithmetic by construction; this pins the two helpers together so an edit to one
        // cannot silently diverge from the other.
        Assert.InRange(viaBandRms, viaApply * 0.9999, viaApply * 1.0001);
    }

    [Fact]
    public void BandRms_OfWhiteNoise_TracksTheBandwidthFraction()
    {
        var h = FirFilter.DesignBandPass(H2LowHz, H2HighHz, SweepSampleRate, MeasurementTaps);
        var noise = Noise(SweepSampleRate, seed: 99, seconds: 4.0);

        var total = FirFilter.Rms(noise);
        var inBand = FirFilter.BandRms(noise, h);

        // Flat PSD: in-band power is the bandwidth fraction of total power. This is the analytic
        // relation the plan's ~10dB AWGN grid offset rests on, so it is asserted, not assumed.
        var expected = total * Math.Sqrt((H2HighHz - H2LowHz) / (SweepSampleRate / 2.0));
        Assert.InRange(inBand, expected * 0.95, expected * 1.05);
    }

    [Fact]
    public void ScalingNoiseToATargetInBandRatio_ReachesThatRatio()
    {
        var h = FirFilter.DesignBandPass(H2LowHz, H2HighHz, SweepSampleRate, MeasurementTaps);
        var signal = Tone(1900, SweepSampleRate, 1.0);
        var noise = Noise(SweepSampleRate, seed: 7, seconds: 1.0);

        foreach (var targetSnrDb in new[] { 20.0, 6.0, 0.0 })
        {
            var signalInBand = FirFilter.BandRms(signal, h);
            var wantedNoiseInBand = signalInBand / Math.Pow(10.0, targetSnrDb / 20.0);
            var scale = wantedNoiseInBand / FirFilter.BandRms(noise, h);

            var scaled = new float[noise.Length];
            for (var i = 0; i < noise.Length; i++)
            {
                scaled[i] = (float)(noise[i] * scale);
            }

            var achievedDb = 20.0 * Math.Log10(signalInBand / FirFilter.BandRms(scaled, h));
            Assert.InRange(achievedDb, targetSnrDb - 0.01, targetSnrDb + 0.01);
        }
    }

    [Fact]
    public void Resampler_PreservesCorpusBandPower_AndAddsNothingAbove()
    {
        // The corpus is band-limited to about 3.5kHz, so this is the only content the resampler has
        // to carry. Verification item 2 of the plan. The source is cut at 3000Hz rather than 3500 so
        // that the "nothing above 3.6kHz" assertion sees resampler images, not the source filter's
        // own transition skirt.
        var lowPass48 = FirFilter.DesignLowPass(3000, CorpusSampleRate, MeasurementTaps);
        var source = FirFilter.Apply(Noise(CorpusSampleRate, seed: 2026, seconds: 3.0), lowPass48);

        var resampled = PolyphaseResampler.Resample(source, CorpusSampleRate);

        // Trim both ends: the head carries the prototype's warm-up, and the tail runs off the input.
        var trim = PolyphaseResampler.OutputWarmupSamples + 2000;
        var inner = resampled[trim..^trim];
        var sourceInner = source[(trim * CorpusSampleRate / SweepSampleRate)..^(trim * CorpusSampleRate / SweepSampleRate)];

        var beforeBand = FirFilter.BandRms(sourceInner, FirFilter.DesignBandPass(100, 3000, CorpusSampleRate, MeasurementTaps));
        var afterBand = FirFilter.BandRms(inner, FirFilter.DesignBandPass(100, 3000, SweepSampleRate, MeasurementTaps));
        var deltaDb = 20.0 * Math.Log10(afterBand / beforeBand);
        Assert.InRange(deltaDb, -0.1, 0.1);

        var aboveBand = FirFilter.BandRms(inner, FirFilter.DesignBandPass(3600, 20000, SweepSampleRate, MeasurementTaps));
        var aboveDb = 20.0 * Math.Log10(aboveBand / afterBand);
        Assert.True(aboveDb < -60.0, $"energy above 3.6kHz after resampling: {aboveDb:F1}dB relative to in-band");
    }

    [Fact]
    public void Resampler_PreservesToneFrequency()
    {
        var resampled = PolyphaseResampler.Resample(Tone(1900, CorpusSampleRate, 2.0), CorpusSampleRate);

        var trim = PolyphaseResampler.OutputWarmupSamples + 2000;
        var inner = resampled[trim..^trim];

        // A 1900Hz tone must still be 1900Hz at the new rate: it survives a band containing 1900
        // essentially unchanged, and is absent from an equally wide band that excludes it. Both bands
        // are wider than the measurement filter's own transition, or the filter would attenuate the
        // tone it is meant to pass.
        var atTone = FirFilter.BandRms(inner, FirFilter.DesignBandPass(1500, 2300, SweepSampleRate, MeasurementTaps));
        var offTone = FirFilter.BandRms(inner, FirFilter.DesignBandPass(2600, 3400, SweepSampleRate, MeasurementTaps));

        Assert.True(atTone > 0.69, $"1900Hz band after resampling: {atTone:F3}");
        Assert.True(offTone < atTone * 0.01, $"2100Hz band after resampling: {offTone:E3}");
    }

    [Fact]
    public void Resampler_OutputLength_MatchesTheRationalRatio()
    {
        var input = new float[CorpusSampleRate * 2];

        var resampled = PolyphaseResampler.Resample(input, CorpusSampleRate);

        Assert.Equal(SweepSampleRate * 2, resampled.Length);
    }

    [Fact]
    public void DesignLowPass_RejectsAnEvenTapCount()
    {
        // Linear phase needs an odd length, so the group delay is an integer number of samples.
        Assert.Throws<ArgumentOutOfRangeException>(() => FirFilter.DesignLowPass(3500, SweepSampleRate, 1024));
    }

    private static float[] Tone(double frequencyHz, int sampleRate, double seconds)
    {
        var samples = new float[(int)(sampleRate * seconds)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)Math.Sin(2.0 * Math.PI * frequencyHz * i / sampleRate);
        }

        return samples;
    }

    private static float[] Noise(int sampleRate, int seed, double seconds)
    {
        var random = new Random(seed);
        var samples = new float[(int)(sampleRate * seconds)];
        for (var i = 0; i < samples.Length; i++)
        {
            var u1 = 1.0 - random.NextDouble();
            var u2 = random.NextDouble();
            samples[i] = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }

        return samples;
    }
}
