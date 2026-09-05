namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>The floor rule decides every headline number this harness produces, and the equivalent
/// rule in the AWGN sweep shipped with a real defect once: a later, worse-SNR point that happened to
/// pass could resurrect a floor an earlier point had already broken.</summary>
public sealed class RealNoiseFloorRuleTests
{
    private const int Required = 4;

    [Fact]
    public void Floor_IsTheLowestSnrMeetingTheBar_WhenEveryPointAboveAlsoMeetsIt()
    {
        double? floor = RealNoiseImpairmentSweepHarness.ComputeFloor(
            [(40, 5), (30, 5), (20, 5), (12, 4), (6, 1), (0, 0)], Required);

        Assert.Equal(12.0, floor);
    }

    [Fact]
    public void ALaterPassingPoint_CannotResurrectABrokenFloor()
    {
        // 12dB fails the bar, so 6dB passing afterwards is a non-monotonic fluke, not a floor.
        double? floor = RealNoiseImpairmentSweepHarness.ComputeFloor(
            [(40, 5), (30, 5), (20, 5), (12, 3), (6, 5), (0, 0)], Required);

        Assert.Equal(20.0, floor);
    }

    [Fact]
    public void ThreeOfFive_DoesNotMeetTheFourOfFiveBar()
    {
        double? floor = RealNoiseImpairmentSweepHarness.ComputeFloor([(40, 3)], Required);

        Assert.Null(floor);
    }

    [Fact]
    public void NoFloor_WhenEvenTheCleanestPointFails()
    {
        double? floor = RealNoiseImpairmentSweepHarness.ComputeFloor(
            [(40, 0), (30, 0), (20, 0)], Required);

        Assert.Null(floor);
    }

    [Fact]
    public void TheShippedRobot36Curve_ReproducesItsRecordedFloor()
    {
        // Straight from the first real-corpus run: 5,5,5,5,5,3,1,0,0,0 down the sweep grid, which the
        // report recorded as a 16dB floor.
        double? floor = RealNoiseImpairmentSweepHarness.ComputeFloor(
            [(40, 5), (30, 5), (25, 5), (20, 5), (16, 5), (12, 3), (9, 1), (6, 0), (3, 0), (0, 0)], Required);

        Assert.Equal(16.0, floor);
    }

    [Fact]
    public void TheOldTwoSeedRuleWouldHaveCalledTheFloorFourStepsPessimistically()
    {
        // Same curve, judged by "every seed must be usable". This is the bias the 4-of-5 rule exists
        // to remove: one unlucky realization at 16dB would have set the floor at 25dB instead.
        double? strict = RealNoiseImpairmentSweepHarness.ComputeFloor(
            [(40, 5), (30, 5), (25, 5), (20, 5), (16, 4), (12, 3), (9, 1), (6, 0), (3, 0), (0, 0)], requiredUsable: 5);
        double? fourOfFive = RealNoiseImpairmentSweepHarness.ComputeFloor(
            [(40, 5), (30, 5), (25, 5), (20, 5), (16, 4), (12, 3), (9, 1), (6, 0), (3, 0), (0, 0)], Required);

        Assert.Equal(20.0, strict);
        Assert.Equal(16.0, fourOfFive);
    }
}
