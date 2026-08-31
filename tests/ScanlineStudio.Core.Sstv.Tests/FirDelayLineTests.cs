namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Isolated unit tests for <see cref="FirDelayLine"/> itself -- tested independently of either real
/// filter that uses it, per this project's chop-into-pieces methodology. The bit-exact parity gate
/// against each real filter's own PRE-circular-buffer behavior lives in
/// <c>SearchBandpassFilterCircularBufferParityTests.cs</c> and
/// <c>HilbertFmDemodulatorCircularBufferParityTests.cs</c> -- this file only pins the class's own
/// contract (push direction, logical indexing, convolution) against a naive from-scratch reference,
/// independent of any filter's own coefficient conventions.
/// </summary>
public class FirDelayLineTests
{
    // > 2x capacity (tap=4, capacity=5) so both wrap-around and the very first (zero-padded) calls are exercised.
    private static readonly double[] Inputs =
        [1.5, -2.5, 3.5, -4.5, 5.5, -6.5, 7.5, -8.5, 9.5, -10.5, 11.5];

    [Fact]
    public void PushForward_ThenConvolve_MatchesNaiveOldestFirstLinearReference()
    {
        // headMovesForward: true -- HilbertFmDemodulator's own convention (Array.Copy(z, 1, z, 0, tap); z[tap] = input;).
        const int tap = 4;
        var h = new double[] { 1.0, 2.0, 3.0, 4.0, 5.0 };
        var line = new FirDelayLine(tap, headMovesForward: true);
        var z = new double[tap + 1];

        foreach (var input in Inputs)
        {
            line.Push(input);
            Array.Copy(z, 1, z, 0, tap);
            z[tap] = input;

            var expected = 0.0;
            for (var i = 0; i <= tap; i++)
            {
                expected += z[i] * h[i];
            }

            Assert.Equal(expected, line.Convolve(h));
        }
    }

    [Fact]
    public void PushBackward_ThenConvolve_MatchesNaiveNewestFirstLinearReference()
    {
        // headMovesForward: false -- SearchBandpassFilter's own convention (Array.Copy(_z, 0, _z, 1, _tap); _z[0] = input;).
        const int tap = 4;
        var h = new double[] { 1.0, 2.0, 3.0, 4.0, 5.0 };
        var line = new FirDelayLine(tap, headMovesForward: false);
        var z = new double[tap + 1];

        foreach (var input in Inputs)
        {
            line.Push(input);
            Array.Copy(z, 0, z, 1, tap);
            z[0] = input;

            var expected = 0.0;
            for (var i = 0; i <= tap; i++)
            {
                expected += z[i] * h[i];
            }

            Assert.Equal(expected, line.Convolve(h));
        }
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    [InlineData(true, 4)]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(false, 4)]
    public void Indexer_LogicalIndex_EqualsConvolveWithUnitCoefficientAtThatIndex(bool headMovesForward, int logicalIndex)
    {
        // Proves the indexer and Convolve agree on which physical slot a given logical index maps to
        // -- the exact property HilbertFmDemodulator.ProcessSample's own `_z[_htap]` point-read outside
        // Convolve depends on (plan-review round-1 blocker: a raw physical-index read would silently
        // diverge from Convolve's own logical addressing once the head moves).
        const int tap = 4;
        var line = new FirDelayLine(tap, headMovesForward: headMovesForward);
        foreach (var input in Inputs) // wraps at least once (capacity=5)
        {
            line.Push(input);
        }

        var h = new double[tap + 1];
        h[logicalIndex] = 1.0;

        Assert.Equal(line[logicalIndex], line.Convolve(h));
    }

    [Fact]
    public void Tap_ReflectsConstructorArgument()
    {
        var line = new FirDelayLine(tap: 17, headMovesForward: true);

        Assert.Equal(17, line.Tap);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)] // Tap+1, one past the valid range [0, Tap]
    public void Indexer_OutOfRangeLogicalIndex_Throws(int logicalIndex)
    {
        // Code-review finding: the old raw double[] this replaced would throw
        // IndexOutOfRangeException on an out-of-range access; a bare Debug.Assert here would be
        // silently inert in Release, reintroducing a wrong-slot-not-a-throw risk on any future caller.
        var line = new FirDelayLine(tap: 4, headMovesForward: true);

        Assert.Throws<ArgumentOutOfRangeException>(() => line[logicalIndex]);
    }
}
