using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.UI.Controls;

namespace ScanlineStudio.UI.Tests;

public sealed class SpectrumTraceMathTests
{
    [Fact]
    public void MapFrequencyToX_AtStartHz_ReturnsZero()
    {
        var x = SpectrumTraceMath.MapFrequencyToX(freqHz: 1000, startHz: 1000, spanHz: 1600, width: 400);
        Assert.Equal(0, x);
    }

    [Fact]
    public void MapFrequencyToX_AtStartPlusSpan_ReturnsWidth()
    {
        var x = SpectrumTraceMath.MapFrequencyToX(freqHz: 2600, startHz: 1000, spanHz: 1600, width: 400);
        Assert.Equal(400, x);
    }

    [Fact]
    public void MapFrequencyToX_AtMidpoint_ReturnsHalfWidth()
    {
        var x = SpectrumTraceMath.MapFrequencyToX(freqHz: 1800, startHz: 1000, spanHz: 1600, width: 400);
        Assert.Equal(200, x);
    }

    [Fact]
    public void MapFrequencyToX_ZeroSpan_DoesNotDivideByZero()
    {
        var x = SpectrumTraceMath.MapFrequencyToX(freqHz: 1800, startHz: 1000, spanHz: 0, width: 400);
        Assert.Equal(0, x);
    }

    [Fact]
    public void MapDbToY_AtZeroDb_ReturnsHeight_Bottom()
    {
        // ZeroDb=0/GainDb=50: a 0dB reading is the floor -> bottom of the trace (Y=height).
        var y = SpectrumTraceMath.MapDbToY(db: 0, zeroDb: 0, gainDb: 50, height: 200);
        Assert.Equal(200, y);
    }

    [Fact]
    public void MapDbToY_AtZeroDbPlusGainDb_ReturnsZero_Top()
    {
        var y = SpectrumTraceMath.MapDbToY(db: 50, zeroDb: 0, gainDb: 50, height: 200);
        Assert.Equal(0, y);
    }

    [Fact]
    public void MapDbToY_GainDbZero_DoesNotDivideByZero()
    {
        var y = SpectrumTraceMath.MapDbToY(db: 25, zeroDb: 0, gainDb: 0, height: 200);
        Assert.Equal(200, y);
    }

    [Fact]
    public void DecayPeak_NewValueHigherThanCurrentPeak_ImmediatelyJumpsToNewValue()
    {
        var result = SpectrumTraceMath.DecayPeak(currentPeakDb: 10, newDb: 40, elapsedSeconds: 0.05);
        Assert.Equal(40, result);
    }

    [Fact]
    public void DecayPeak_NewValueLowerThanCurrentPeak_DecaysByRateTimesElapsed()
    {
        var result = SpectrumTraceMath.DecayPeak(currentPeakDb: 40, newDb: 0, elapsedSeconds: 1.0, decayDbPerSecond: 36.0);
        Assert.Equal(4, result);
    }

    [Fact]
    public void DecayPeak_DecayCannotFallBelowTheNewReading()
    {
        // A long elapsed gap must not decay past the CURRENT reading -- the peak can't be lower
        // than what's actually being received right now.
        var result = SpectrumTraceMath.DecayPeak(currentPeakDb: 40, newDb: 20, elapsedSeconds: 100.0, decayDbPerSecond: 36.0);
        Assert.Equal(20, result);
    }

    [Fact]
    public void DecayPeak_IsFrameRateIndependent_SameElapsedTimeGivesSameResult()
    {
        // Auditor-caught (batch 8 plan review): a frame-COUNT-based decay would decay faster at a
        // higher frame rate for the same wall-clock time. This must not.
        var oneFrameOfOneSecond = SpectrumTraceMath.DecayPeak(currentPeakDb: 40, newDb: 0, elapsedSeconds: 1.0);
        var tenFramesSummingToOneSecond = 40.0;
        for (var i = 0; i < 10; i++)
        {
            tenFramesSummingToOneSecond = SpectrumTraceMath.DecayPeak(tenFramesSummingToOneSecond, newDb: 0, elapsedSeconds: 0.1);
        }

        Assert.Equal(oneFrameOfOneSecond, tenFramesSummingToOneSecond, precision: 6);
    }

    [Fact]
    public void ComputeMarkerFrequencies_NoModeLocked_ReturnsTheWideModeDefaultSet()
    {
        var markers = SpectrumTraceMath.ComputeMarkerFrequencies(null);

        var frequencies = markers.Select(m => m.FrequencyHz).OrderBy(f => f).ToList();
        Assert.Equal([1200.0, 1500.0, 1900.0, 2300.0], frequencies);
        Assert.True(markers.Single(m => m.FrequencyHz == 1200.0).IsSync);
        Assert.False(markers.Single(m => m.FrequencyHz == 1900.0).IsSync);
    }

    [Fact]
    public void ComputeMarkerFrequencies_WideModeLocked_MatchesTheIdleDefaultSetExactly()
    {
        // Auditor-caught (batch 8 plan review): an earlier version used just {sync, LuminanceMinHz,
        // LuminanceMaxHz} (3 elements for a wide mode), which made 1900 vanish the instant a wide
        // mode locked and reappear on idle -- the idle default and a locked wide mode must show
        // IDENTICAL markers, matching legacy's own wide-mode set (Main.cpp:3269-3291).
        var wideMode = new SstvModeDefinition(
            Id: "sc1", DisplayName: "Scottie 1", VisCode: 60, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 138.24)]);

        var idleMarkers = SpectrumTraceMath.ComputeMarkerFrequencies(null);
        var lockedMarkers = SpectrumTraceMath.ComputeMarkerFrequencies(wideMode);

        Assert.Equal(
            idleMarkers.Select(m => (m.FrequencyHz, m.IsSync)).OrderBy(m => m.FrequencyHz),
            lockedMarkers.Select(m => (m.FrequencyHz, m.IsSync)).OrderBy(m => m.FrequencyHz));
    }

    [Fact]
    public void ComputeMarkerFrequencies_NarrowModeLocked_UsesTheNarrowSyncAndLuminanceTones()
    {
        // Includes 1200 (auditor round-3 fix): legacy draws a dotted 1200Hz marker even in narrow
        // mode (Main.cpp:3269-3270) alongside the real 1900Hz narrow sync tone.
        var narrowMode = new SstvModeDefinition(
            Id: "mr73", DisplayName: "MR73", VisCode: 0, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 138.24)],
            LuminanceMinHz: 2044, LuminanceMaxHz: 2300,
            NarrowModeCode: 5);

        var markers = SpectrumTraceMath.ComputeMarkerFrequencies(narrowMode);

        var frequencies = markers.Select(m => m.FrequencyHz).OrderBy(f => f).ToList();
        Assert.Equal([1200.0, 1500.0, 1900.0, 2044.0, 2300.0], frequencies);
        Assert.True(markers.Single(m => m.FrequencyHz == 1900.0).IsSync);
        Assert.False(markers.Single(m => m.FrequencyHz == 1200.0).IsSync);
        Assert.False(markers.Single(m => m.FrequencyHz == 2044.0).IsSync);
    }
}
