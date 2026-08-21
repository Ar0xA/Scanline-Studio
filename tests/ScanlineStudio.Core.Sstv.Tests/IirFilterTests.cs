namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Closes a coverage gap flagged by Tier A Batch 7 chunk 7a (docs/functional-audit-playbook.md):
/// <see cref="IirFilter"/> had no direct test anywhere -- only indirect exercise through
/// <see cref="HilbertFmDemodulator"/>. These pin actual output values for both the even-order
/// (no odd tail) and odd-order (with the odd tail) code paths -- both are real production shapes:
/// even via <see cref="SyncEnvelopeDetector"/>'s `Design(50, sampleRate, 2)`, odd via
/// <see cref="PllFmDemodulator"/>'s legacy-matched `outOrder=3`. Values independently computed
/// (Python, double precision) from `CIIR::MakeIIR`/`CIIR::Do`'s exact formulas (`fir.cpp`), not
/// read back from this class's own output.
/// </summary>
public class IirFilterTests
{
    [Fact]
    public void Design_EvenOrder_MatchesIndependentlyComputedValues()
    {
        // 50Hz/2nd-order Butterworth at 11025Hz -- SyncEnvelopeDetector's own real construction.
        var filter = new IirFilter();
        filter.Design(50.0, 11025.0, 2);

        double[] expected =
        [
            0.0001989714060450321,
            0.000986839882089722,
            0.002538690065969809,
            0.00482311181537214,
            0.00780935255372723,
        ];

        foreach (var expectedValue in expected)
        {
            Assert.Equal(expectedValue, filter.Process(1.0), precision: 15);
        }
    }

    [Fact]
    public void Design_OddOrder_ExercisesTheOddTailPath_MatchesIndependentlyComputedValues()
    {
        // 50Hz/3rd-order Butterworth at 11025Hz -- CPLL's real legacy-matched outOrder=3
        // (sstv.cpp:251-254, ported by PllFmDemodulator.cs). The odd-order tail branch
        // (IirFilter.cs:73-84) is otherwise unexercised by any other test in this suite.
        var filter = new IirFilter();
        filter.Design(50.0, 11025.0, 3);

        double[] expected =
        [
            2.8114883314747004e-06,
            1.952019605301508e-05,
            6.901002806112847e-05,
            0.00017187774433558538,
            0.000347493100031763,
        ];

        foreach (var expectedValue in expected)
        {
            Assert.Equal(expectedValue, filter.Process(1.0), precision: 15);
        }
    }
}
