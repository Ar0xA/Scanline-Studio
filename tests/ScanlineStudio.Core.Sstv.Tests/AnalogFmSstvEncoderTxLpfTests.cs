namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Options stub backlog item 3 (docs/plans/options-stub-item3-tx-bpf-lpf-plan.md) -- the TX LPF
/// pre-VCO frequency-smoothing addition to <see cref="AnalogFmSstvEncoder.RenderSegments"/>/
/// <see cref="AnalogFmSstvEncoder.EncodeBatchedAsyncCore"/>. Every assertion below is exact (a stated
/// tolerance or bit-exact), not a vague "behavior differs" check -- a lesson item 2's own auditor
/// code-review round applied here proactively.
/// </summary>
public class AnalogFmSstvEncoderTxLpfTests
{
    private const int SampleRate = 11025;

    [Fact]
    public void RenderSegments_TxLpfDisabled_MatchesTheOldPerSegmentConstantPhaseIncrementFormula()
    {
        // Golden-fixture invariant / regression guard: applyLpf defaults to false, so every
        // pre-existing 2-arg RenderSegments/EncodeAsync call site must keep producing EXACTLY what it
        // did before this item's own refactor moved phaseIncrement's computation inside the per-
        // sample loop. Independently re-derives the OLD per-segment-constant algorithm here (not
        // re-using any of the production code under test) and compares bit-for-bit.
        var segments = new (double FrequencyHz, double DurationMs)[] { (1500.0, 20.0), (1900.0, 15.0), (2300.0, 20.0) };

        var actual = AnalogFmSstvEncoder.RenderSegments(segments, SampleRate, applyFilter: false).ToList();

        var expected = new List<float>();
        var phase = 0.0;
        var idealSamplesSoFar = 0.0;
        var emittedSamples = 0L;
        foreach (var (frequencyHz, durationMs) in segments)
        {
            idealSamplesSoFar += durationMs / 1000.0 * SampleRate;
            var targetEmitted = (long)idealSamplesSoFar;
            var samplesToEmit = targetEmitted - emittedSamples;
            emittedSamples = targetEmitted;
            var phaseIncrement = 2 * Math.PI * frequencyHz / SampleRate; // computed ONCE per segment, the old shape

            for (var i = 0; i < samplesToEmit; i++)
            {
                phase += phaseIncrement;
                if (phase >= 2 * Math.PI)
                {
                    phase -= 2 * Math.PI;
                }

                expected.Add((float)Math.Sin(phase));
            }
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RenderSegments_TxLpfEnabled_MatchesAnIndependentlyComputedSmoothedPhaseTrajectory()
    {
        // Exact, non-vacuous parity assertion (item 2's own auditor lesson): independently re-derive
        // the EXPECTED phase trajectory using a fresh reference MovingAverage fed the same raw
        // frequency sequence RenderSegments itself consumes, then compare bit-for-bit against the
        // real implementation's actual output.
        const double lpfFrequencyHz = 2205.0; // window = (int)(11025/2205 + 0.5) = 5, hand-pinned
        var segments = new (double FrequencyHz, double DurationMs)[] { (1500.0, 50.0), (2300.0, 50.0) };

        var actual = AnalogFmSstvEncoder.RenderSegments(segments, SampleRate, applyFilter: false, applyLpf: true, lpfFrequencyHz: lpfFrequencyHz).ToList();

        var reference = new MovingAverage(5);
        var expected = new List<float>();
        var phase = 0.0;
        var idealSamplesSoFar = 0.0;
        var emittedSamples = 0L;
        foreach (var (frequencyHz, durationMs) in segments)
        {
            idealSamplesSoFar += durationMs / 1000.0 * SampleRate;
            var targetEmitted = (long)idealSamplesSoFar;
            var samplesToEmit = targetEmitted - emittedSamples;
            emittedSamples = targetEmitted;

            for (var i = 0; i < samplesToEmit; i++)
            {
                var smoothedFrequencyHz = reference.Add(frequencyHz);
                var phaseIncrement = 2 * Math.PI * smoothedFrequencyHz / SampleRate;
                phase += phaseIncrement;
                if (phase >= 2 * Math.PI)
                {
                    phase -= 2 * Math.PI;
                }

                expected.Add((float)Math.Sin(phase));
            }
        }

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(SampleRate, 2000.0, 6)] // real legacy default -- +0.5 vs bare truncation genuinely
                                         // disagree here (11025/2000=5.5125, +0.5=6.0125->6, bare
                                         // truncation would give 5): code-review round 1 finding, the
                                         // earlier test suite's own 2205Hz choice happened to make
                                         // both formulas agree (11025/2205=5.0 exactly), so nothing
                                         // actually exercised the +0.5 rounding at a value where it
                                         // matters until this test.
    [InlineData(SampleRate, 2205.0, 5)] // exact division -- both formulas agree here, kept as a
                                         // second data point matching the other tests in this file.
    [InlineData(SampleRate, 100.0, 110)] // floor of the real legacy range (Option.cpp:452-459) --
                                          // 11025/100=110.25, +0.5=110.75, truncates to 110
    [InlineData(SampleRate, 3000.0, 4)] // ceiling of the real legacy range
    public void ResolveLpfWindowSize_MatchesLegacysRoundingFormula_NotZeroCrossingsBareTruncation(double sampleRate, double lpfFrequencyHz, int expectedWindowSize)
    {
        Assert.Equal(expectedWindowSize, AnalogFmSstvEncoder.ResolveLpfWindowSize(sampleRate, lpfFrequencyHz));
    }

    [Fact]
    public void MovingAverage_StepBetweenValues_ProducesTheExactBoxcarLinearRamp()
    {
        // The specific identity CSmooz/MovingAverage guarantees: sample k after a step from `old` to
        // `new`, with the window already fully warmed up on `old`, equals (k*new + (N-k)*old)/N,
        // reaching `new` EXACTLY at sample N -- NOT an "asymptotically approaching" exponential decay
        // (round-1 plan-review finding: that phrasing would also pass against a wrong IIR
        // implementation). Verified on the RAW frequency (via a reference MovingAverage), not the
        // nonlinear sine output, which phase-integrates the ramp rather than exposing it directly.
        const int windowSize = 5; // matches lpfFrequencyHz=2205.0 at 11025Hz in the other tests here
        const double oldFrequencyHz = 1500.0;
        const double newFrequencyHz = 2300.0;

        var reference = new MovingAverage(windowSize);
        for (var i = 0; i < windowSize; i++) // warm-up: fill the window with `old` BEFORE the step
        {
            var warmed = reference.Add(oldFrequencyHz);
            Assert.Equal(oldFrequencyHz, warmed, precision: 9);
        }

        for (var k = 1; k <= windowSize; k++)
        {
            var actual = reference.Add(newFrequencyHz);
            var expected = (k * newFrequencyHz + (windowSize - k) * oldFrequencyHz) / windowSize;
            Assert.Equal(expected, actual, precision: 9);
        }

        // Exactly at sample N (k=windowSize), the ramp has fully reached the new value.
        Assert.Equal(newFrequencyHz, reference.Add(newFrequencyHz), precision: 9);
    }

    [Fact]
    public void RenderSegments_TxLpfEnabled_SilenceDoesNotDisturbTheMovingAveragesHeldState()
    {
        // sstv.cpp:2867's if(f>0) gate skips avgLPF.Avg during silence -- its history holds through
        // the gap and resumes smoothing (not resets) once a real tone follows. Verified via the same
        // independent-reference-MovingAverage technique as the tests above: a silence segment does
        // NOT get an Add call in the reference either, and the two stay bit-identical across the gap.
        const double lpfFrequencyHz = 2205.0; // window = 5
        var segments = new (double FrequencyHz, double DurationMs)[] { (1500.0, 20.0), (0.0, 10.0), (2300.0, 20.0) };

        var actual = AnalogFmSstvEncoder.RenderSegments(segments, SampleRate, applyFilter: false, applyLpf: true, lpfFrequencyHz: lpfFrequencyHz).ToList();

        var reference = new MovingAverage(5);
        var expected = new List<float>();
        var phase = 0.0;
        var idealSamplesSoFar = 0.0;
        var emittedSamples = 0L;
        foreach (var (frequencyHz, durationMs) in segments)
        {
            idealSamplesSoFar += durationMs / 1000.0 * SampleRate;
            var targetEmitted = (long)idealSamplesSoFar;
            var samplesToEmit = targetEmitted - emittedSamples;
            emittedSamples = targetEmitted;

            for (var i = 0; i < samplesToEmit; i++)
            {
                double sample;
                if (frequencyHz <= 0)
                {
                    sample = 0.0;
                }
                else
                {
                    var smoothedFrequencyHz = reference.Add(frequencyHz); // skipped entirely during silence
                    var phaseIncrement = 2 * Math.PI * smoothedFrequencyHz / SampleRate;
                    phase += phaseIncrement;
                    if (phase >= 2 * Math.PI)
                    {
                        phase -= 2 * Math.PI;
                    }

                    sample = Math.Sin(phase);
                }

                expected.Add((float)sample);
            }
        }

        Assert.Equal(expected, actual);
    }
}
