namespace ScanlineStudio.Core.Sstv.Tests;

public sealed class ManualSlantProjectionSafetyTests
{
    [Fact]
    public void RealisticCorrection_IsAccepted()
    {
        Assert.True(AnalogFmSstvDecoder.IsManualSlantProjectionSafe(
            candidateSampleRate: 11030.25,
            lineDurationMs: SstvModeRegistry.Robot36.LineDurationMs,
            currentConsumedSample: 1_000_000,
            remainingTransmissionLines: 200));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1d)]
    [InlineData(0d)]
    [InlineData(0.49d)]
    [InlineData(2147483647.6d)]
    public void NonRepresentableEffectiveRate_IsRejected(double candidateSampleRate)
    {
        Assert.False(AnalogFmSstvDecoder.IsManualSlantProjectionSafe(
            candidateSampleRate,
            lineDurationMs: SstvModeRegistry.Robot36.LineDurationMs,
            currentConsumedSample: 0,
            remainingTransmissionLines: 1));
    }

    [Fact]
    public void ProjectionPastIntMaximum_IsRejected()
    {
        Assert.False(AnalogFmSstvDecoder.IsManualSlantProjectionSafe(
            candidateSampleRate: 48500,
            lineDurationMs: SstvModeRegistry.ScottieDx.LineDurationMs,
            currentConsumedSample: int.MaxValue - 1000,
            remainingTransmissionLines: 1));
    }

    [Fact]
    public void ProjectionExactlyAtIntMaximum_IsRejectedToPreserveCursorIncrementHeadroom()
    {
        Assert.False(AnalogFmSstvDecoder.IsManualSlantProjectionSafe(
            candidateSampleRate: 1,
            lineDurationMs: 1000,
            currentConsumedSample: int.MaxValue - 2,
            remainingTransmissionLines: 0));
    }
}
