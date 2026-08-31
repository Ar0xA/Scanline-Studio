namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// T1-1 (production_audit.md)'s required gate for <see cref="HilbertFmDemodulator"/>: bit-exact
/// <c>double</c> equality between the real, current circular-buffer-based <c>ProcessSample</c> and a
/// test-local reference reproducing the PRE-change linear-array implementation, sample-for-sample, at
/// every reachable decimation tier.
///
/// Critical scoping note from plan-review round 2: the comparison MUST be at the full
/// <c>ProcessSample</c> level, not just <c>DoFir</c> in isolation. <c>ProcessSample</c> reads its delay
/// line's center tap directly (<c>_z[_htap]</c>, the delayed real component paired with the FIR's
/// quadrature output) OUTSIDE <c>DoFir</c> -- a raw physical-index read of a circular buffer would
/// silently diverge from the correct logical-index value once the head moves, a real blocker plan-review
/// round 1 found and this class's own <see cref="FirDelayLine"/>-typed <c>_z</c> field now fixes by
/// construction. A <c>DoFir</c>-only comparison (or the existing suite's short, all-zero-history tests
/// in <c>HilbertFmDemodulatorTests.cs</c>) would NOT have caught that bug -- confirmed during
/// plan-review: the first several calls after construction have a still-zero delay line regardless of
/// whether the read is correct, so divergence only appears once real, non-zero history reaches the
/// center tap.
/// </summary>
public class HilbertFmDemodulatorCircularBufferParityTests
{
    /// <summary>Literal reproduction of <see cref="HilbertFmDemodulator.ProcessSample"/>'s PRE-change
    /// implementation -- a plain <c>double[]</c> delay line shifted via <c>Array.Copy</c> every call,
    /// including its own raw <c>_z[_htap]</c> center-tap read (correct in the original because a plain
    /// array has no head to move). Uses the real <see cref="HilbertFmDemodulator.MakeHilbert"/>,
    /// <see cref="HilbertFmDemodulator.ComputePhaseDifference"/>, and <see cref="IirFilter"/> (all
    /// unchanged by this item) so the only thing this test isolates is delay-line addressing.</summary>
    private sealed class LinearReferenceDemodulator
    {
        private readonly int _tap;
        private readonly int _htap;
        private readonly int _df;
        private readonly double _offWide;
        private readonly double _outWide;
        private readonly double _offNarrow;
        private readonly double _outNarrow;
        private readonly double[] _h;
        private readonly double[] _z;
        private readonly double[] _a = new double[4];
        private readonly IirFilter _smoothingFilter = new();

        public LinearReferenceDemodulator(int sampleRate)
        {
            int tap;
            int df;
            if (sampleRate >= 40000)
            {
                tap = 48;
                df = 2;
            }
            else if (sampleRate >= 16000)
            {
                tap = 24;
                df = 1;
            }
            else
            {
                tap = 12;
                df = 0;
            }

            _tap = tap;
            _df = df;
            _htap = tap / 2;

            var tierMultiplier = df switch { 2 => 4.0, 1 => 2.0, _ => 1.0 };
            _offWide = 2 * Math.PI * HilbertFmDemodulator.NormalCenterHz / sampleRate * tierMultiplier;
            _outWide = 32768.0 * sampleRate / (2 * Math.PI * HilbertFmDemodulator.NormalBandwidthHz) / tierMultiplier;
            _offNarrow = 2 * Math.PI * HilbertFmDemodulator.NarrowCenterHz / sampleRate * tierMultiplier;
            _outNarrow = 32768.0 * sampleRate / (2 * Math.PI * HilbertFmDemodulator.NarrowBandwidthHz) / tierMultiplier;

            _h = HilbertFmDemodulator.MakeHilbert(tap, sampleRate, 100.0, sampleRate / 2.0 - 100.0);
            _z = new double[tap + 1];
            _smoothingFilter.Design(1800.0, sampleRate, 3);
        }

        public double ProcessSample(double input, bool isNarrow)
        {
            var quadrature = DoFirLinear(input);
            var real = _z[_htap];
            var phase = real != 0.0 ? Math.Atan2(quadrature, real) : 0.0;

            var diff = HilbertFmDemodulator.ComputePhaseDifference(phase, _a, _df);

            if (diff >= Math.PI)
            {
                diff -= 2 * Math.PI;
            }
            else if (diff <= -Math.PI)
            {
                diff += 2 * Math.PI;
            }

            var centerHz = isNarrow ? HilbertFmDemodulator.NarrowCenterHz : HilbertFmDemodulator.NormalCenterHz;
            var bandwidthHz = isNarrow ? HilbertFmDemodulator.NarrowBandwidthHz : HilbertFmDemodulator.NormalBandwidthHz;
            diff += isNarrow ? _offNarrow : _offWide;

            var scaled = _smoothingFilter.Process(diff * (isNarrow ? _outNarrow : _outWide));
            return centerHz - scaled * bandwidthHz / 32768.0;
        }

        private double DoFirLinear(double input)
        {
            Array.Copy(_z, 1, _z, 0, _tap);
            _z[_tap] = input;

            var sum = 0.0;
            for (var i = 0; i <= _tap; i++)
            {
                sum += _z[i] * _h[i];
            }

            return sum;
        }
    }

    [Theory]
    [InlineData(11025, 12)] // low tier (< 16000Hz)
    [InlineData(22050, 24)] // middle tier (16000-40000Hz) -- reachable in this port only via this test today,
                             // same status as HilbertFmDemodulatorTests' own middle-tier coverage
    [InlineData(44100, 48)] // high tier (>= 40000Hz)
    public void ProcessSample_MatchesLinearReferenceImplementation_BitExact_AcrossManyWrapsAndMidStreamFlip(
        int sampleRate, int expectedTap)
    {
        var candidate = new HilbertFmDemodulator(sampleRate);
        var reference = new LinearReferenceDemodulator(sampleRate);
        Assert.Equal(expectedTap / 2, candidate.HalfTap); // sanity: pins the exact tap this case claims to exercise

        var capacity = expectedTap + 1;
        var sampleCount = (capacity * 10) + 7; // >= 10x capacity, deliberately not an exact multiple of it
        var isNarrowFlipAt = capacity + 3; // mid-stream, deliberately not a multiple of capacity
        var random = new Random(sampleRate); // fixed per case

        for (var i = 0; i < sampleCount; i++)
        {
            // Varied magnitude, not constant/all-zero -- those can't distinguish an index permutation
            // from correct behavior.
            var input = (random.NextDouble() - 0.5) * 40000.0;
            var isNarrow = i >= isNarrowFlipAt;

            var candidateOutput = candidate.ProcessSample(input, isNarrow);
            var referenceOutput = reference.ProcessSample(input, isNarrow);

            Assert.Equal(referenceOutput, candidateOutput); // bit-exact -- no precision/tolerance argument
        }
    }
}
