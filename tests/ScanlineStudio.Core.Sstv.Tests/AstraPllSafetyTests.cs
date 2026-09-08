namespace ScanlineStudio.Core.Sstv.Tests;

public sealed class AstraPllSafetyTests
{
    [Theory]
    [InlineData(-4.0)]
    [InlineData(-3.999)]
    [InlineData(3.999)]
    [InlineData(4.0)]
    [InlineData(4.001)]
    [InlineData(-4.001)]
    public void Oscillator_NormalWrapOrderMatchesLegacyReference(double cycles)
    {
        const int size = 22050;
        var vco = new Vco(11025, 0);
        vco.SetGain(11025);
        var phase = cycles * size;
        while (phase >= size) phase -= size;
        while (phase < 0) phase += size;
        var expected = Math.Sin((int)phase * (2 * Math.PI / size));
        Assert.Equal(expected, vco.Process(cycles));
    }

    [Theory]
    [InlineData(1e25)]
    [InlineData(double.MaxValue)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.NaN)]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidGain_UsesUnityAtSettingsConstructorAndLiveTuning(double gain)
    {
        Assert.Equal(1, new SstvDecoderSettings { PllVcoGain = gain }.Resolve().PllVcoGain);
        var decoder = new AnalogFmSstvDecoder(pllVcoGain: gain);
        Assert.Equal(1, decoder.PllTuningForTests.VcoGain);
        decoder.RequestPllTuning(gain, 1, 1500, 3, 900);
        decoder.PushSamples(new float[2]);
        Assert.Equal(1, decoder.PllTuningForTests.VcoGain);
        var actual = new PllFmDemodulator(11025, 1500, 2300, gain);
        var expected = new PllFmDemodulator(11025, 1500, 2300);
        actual.SetTuning(gain, 1, 1500, 3, 900);
        expected.SetTuning(1, 1, 1500, 3, 900);
        for (var i = 0; i < 100; i++) Assert.Equal(expected.ProcessSample(1), actual.ProcessSample(1));
    }

    [Theory]
    [InlineData(0.35)]
    [InlineData(1)]
    [InlineData(10)]
    public void SupportedGain_IsPreserved(double gain) =>
        Assert.Equal(gain, new SstvDecoderSettings { PllVcoGain = gain }.Resolve().PllVcoGain);

    [Theory]
    [InlineData(1e25)]
    [InlineData(-1e25)]
    [InlineData(double.MaxValue)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.NaN)]
    public void Oscillator_ExtremeInputTerminatesAndRecovers(double input)
    {
        var vco = new Vco(11025, 1900);
        vco.SetGain(-800);
        Assert.True(double.IsFinite(vco.Process(input)));
        Assert.True(double.IsFinite(vco.Process(0)));
        vco.SetGain(input);
        Assert.True(double.IsFinite(vco.Process(1)));
    }
}
