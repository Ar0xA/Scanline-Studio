namespace ScanlineStudio.Core.Sstv.Tests;

public class TxToneSynthesizerTests
{
    private const double NominalRate = 11025.0;

    [Theory]
    [InlineData(2005.0, 12.5, 5)] // effective rate would give 6
    [InlineData(2004.0, -7.3, 6)] // effective rate would give 5
    public void LpfWindow_IsSizedFromTheNominalRate_NotTheEffectiveRate(
        double lpfFrequencyHz, double sampleRateOffsetHz, int expectedWindow)
    {
        var effectiveRate = NominalRate + sampleRateOffsetHz;
        Assert.NotEqual(expectedWindow, AnalogFmSstvEncoder.ResolveLpfWindowSize(effectiveRate, lpfFrequencyHz));

        var synthesizer = new TxToneSynthesizer(
            NominalRate,
            effectiveRate,
            carrierOffsetHz: 0.0,
            bpfEnabled: true,
            bpfTapCount: TxOutputBandpassFilter.DefaultTapCount,
            lpfEnabled: true,
            lpfFrequencyHz: lpfFrequencyHz);

        Assert.Equal(expectedWindow, synthesizer.LpfWindowSize);
    }
}
