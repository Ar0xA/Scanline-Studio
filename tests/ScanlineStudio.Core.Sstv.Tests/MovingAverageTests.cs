namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Closes a coverage gap flagged by Tier A Batch 7 chunk 7a (docs/functional-audit-playbook.md):
/// <see cref="MovingAverage"/> had no direct test anywhere -- only the single-value arithmetic
/// check in SlantTests.cs. Exercises <c>Add</c>'s ring-buffer fill/wraparound, <c>Reset</c>'s
/// instant full-seed, and <c>Clear</c>'s true-empty-restart -- the three distinct behaviors
/// <c>CSmooz::Avg</c>/<c>SetData</c>/<c>SetCount</c> (`sstv.h:80-145`) provide.
/// </summary>
public class MovingAverageTests
{
    [Fact]
    public void Add_FillsGraduallyThenWrapsAroundTheRingBuffer()
    {
        var average = new MovingAverage(3);

        Assert.Equal(2.0, average.Add(2.0), precision: 12); // count=1: avg of [2]
        Assert.Equal(3.0, average.Add(4.0), precision: 12); // count=2: avg of [2,4]
        Assert.Equal(4.0, average.Add(6.0), precision: 12); // count=3: avg of [2,4,6]
        Assert.Equal(6.0, average.Add(8.0), precision: 12); // wraps: overwrites the 2 -> avg of [8,4,6]
    }

    [Fact]
    public void Reset_InstantlySeedsEveryTableSlotWithOneValue()
    {
        // CSmooz::SetData -- unlike Add's gradual fill, Reset makes the average immediately reflect
        // the seed value at full strength (not diluted by an otherwise mostly-empty buffer).
        var average = new MovingAverage(3);

        var seeded = average.Reset(10.0);
        Assert.Equal(10.0, seeded, precision: 12);

        // Adding one new value only replaces ONE of the three seeded slots.
        Assert.Equal(20.0 / 3.0, average.Add(0.0), precision: 12);
    }

    [Fact]
    public void Clear_RestartsFromGenuinelyEmpty_NotFromTheOldSeed()
    {
        // CSmooz::SetCount(n) where n == existing capacity (sstv.h:125-127's else branch) --
        // distinct from Reset: the NEXT Add reflects only its own single input, no seeded dilution.
        var average = new MovingAverage(3);
        average.Reset(10.0);

        average.Clear();

        Assert.Equal(5.0, average.Add(5.0), precision: 12);
    }

    // Options stub backlog item 2 (docs/plans/options-stub-item2-zerocrossing-tuning-plan.md):
    // SetCount is the one CSmooz operation MovingAverage didn't have yet -- ZeroCrossingFrequencyCounter's
    // own SetTuning needs it to resize/reset the FIR smoothing window on demand.

    [Fact]
    public void SetCount_ToANewSize_ReallocatesAndClears()
    {
        var average = new MovingAverage(3);
        average.Add(10.0);
        average.Add(10.0);
        average.Add(10.0); // full: avg == 10

        average.SetCount(5);

        // New, larger window, genuinely empty -- reflects only the single new value.
        Assert.Equal(4.0, average.Add(4.0), precision: 12);
        Assert.Equal(3.0, average.Add(2.0), precision: 12); // avg of [4,2]
    }

    [Fact]
    public void SetCount_ToTheSameSize_StillClears()
    {
        // sstv.h:125-127's else branch, same real legacy quirk Clear_RestartsFromGenuinelyEmpty above
        // exercises via the parameterless Clear() -- SetCount itself must reproduce it too, since
        // ZeroCrossingFrequencyCounter.SetTuning calls SetCount unconditionally on every apply, even
        // when the resolved window size hasn't changed.
        var average = new MovingAverage(3);
        average.Add(10.0);
        average.Add(10.0);
        average.Add(10.0); // full: avg == 10

        average.SetCount(3);

        Assert.Equal(5.0, average.Add(5.0), precision: 12);
    }

    [Fact]
    public void SetCount_Zero_FloorsToOne()
    {
        // CSmooz::SetCount: `if (!n) n = 1;`
        var average = new MovingAverage(3);

        average.SetCount(0);

        Assert.Equal(7.0, average.Add(7.0), precision: 12);
        Assert.Equal(9.0, average.Add(9.0), precision: 12); // window size 1: the OLD value is gone
    }
}
