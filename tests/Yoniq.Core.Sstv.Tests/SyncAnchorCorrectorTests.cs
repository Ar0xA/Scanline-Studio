namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// Piece 8b unit tests for <see cref="SyncAnchorCorrector.ComputeAnchorCorrection"/> -- isolated,
/// synthetic-data tests before this function is wired into the decoder (piece 8c). See
/// <see cref="SyncAnchorCorrector"/>'s own doc comment for the sign-derivation reasoning these tests
/// confirm empirically.
/// </summary>
public class SyncAnchorCorrectorTests
{
    [Fact]
    public void RecoversInjectedSpikeOffset_NegativeDelta()
    {
        // TW=100 (whole number, no fractional creep to worry about here), a periodic spike at bin 10
        // repeated across 4 lines, OFP=20 -- expect argmax=10, delta = 10-20 = -10.
        var delta = SyncAnchorCorrector.ComputeAnchorCorrection(
            lineWidthSamples: 100.0,
            syncPeakOffsetSamples: 20.0,
            lineCount: 4,
            envelopeAt: n => (n % 100) == 10 ? 1000.0 : 0.0);

        Assert.Equal(-10, delta);
    }

    [Fact]
    public void RecoversInjectedSpikeOffset_PositiveDelta()
    {
        // Spike at bin 80, OFP=20 -- delta = 80-20 = 60. No wraparound in this function anymore --
        // see the class doc comment for why the old Scottie-only wraparound branch was removed (it
        // was a mistranslation of legacy's own convention against this port's differently-anchored
        // origin; the caller now folds the needed offset into syncPeakOffsetSamples instead).
        var delta = SyncAnchorCorrector.ComputeAnchorCorrection(
            lineWidthSamples: 100.0,
            syncPeakOffsetSamples: 20.0,
            lineCount: 4,
            envelopeAt: n => (n % 100) == 80 ? 1000.0 : 0.0);

        Assert.Equal(60, delta);
    }

    [Fact]
    public void FractionalLineWidth_BinPlacementUsesTrueModulus_NotTruncatedPageWidth()
    {
        // TW=1653.75 (Robot 36's real value at 11025Hz: 150.0ms/1000*11025), pageWidthSamples=1653
        // (the truncated int(TW) legacy actually uses for the fold's page-iteration bound,
        // Main.cpp:3764/sstv.cpp:594). A single spike at absolute index 2153 (page 1, local offset
        // 500 within a 1653-wide page) should land in bin floor(2153 mod 1653.75) = floor(499.25) =
        // 499 -- NOT bin 500, which is what an implementation that (incorrectly) used the truncated
        // integer page width as the modulus divisor would produce instead. This is the fractional
        // "creep" legacy's own real algorithm has (the fold's bin divisor is the true fractional TW,
        // even though the per-page sample count it iterates is the truncated int(TW)) -- confirming
        // this port reproduces that creep rather than "cleaning it up" into an invented, cleaner
        // computation legacy doesn't actually do.
        var delta = SyncAnchorCorrector.ComputeAnchorCorrection(
            lineWidthSamples: 1653.75,
            syncPeakOffsetSamples: 0.0, // isolate the bin-placement question; no OFP shift
            lineCount: 2,
            envelopeAt: n => n == 2153 ? 1000.0 : 0.0);

        Assert.Equal(499, delta);
    }
}
