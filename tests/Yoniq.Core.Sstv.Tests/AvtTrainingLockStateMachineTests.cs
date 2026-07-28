namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// Isolated tests for <see cref="AvtTrainingLockStateMachine"/>, driven with real demodulated
/// frequencies (audio rendered from <see cref="VisHeader.GenerateAvtSegments"/>, run through the
/// same <see cref="PllFmDemodulator"/> this port's decoder actually uses) rather than hand-fed
/// "perfect" Hz values -- catching any real mismatch between the ported thresholds and actual
/// demodulator behavior, not just the arithmetic in isolation.
/// </summary>
public class AvtTrainingLockStateMachineTests
{
    private const int SampleRate = 11025;

    [Fact]
    public void FullTrainingSequence_CompletesWithinExpectedBudget()
    {
        var demodulated = RenderTrainingSequenceAsDemodulatedHz(SstvModeRegistry.Avt.VisCode);

        var machine = new AvtTrainingLockStateMachine(SampleRate);
        int? completedAt = null;
        for (var i = 0; i < demodulated.Length; i++)
        {
            completedAt = machine.ProcessSample(demodulated[i]);
            if (completedAt is not null)
            {
                break;
            }
        }

        Assert.NotNull(completedAt);

        // Measured, not assumed: a real, sustained lock through the whole 32-block sequence
        // finishes at sample 58608 (~5316.9ms), right at the training sequence's own real content
        // duration (58568 samples, ~5312.9ms) -- not anywhere near the full ~7141ms worst-case
        // nominal budget (78732 samples) a signal with no lock at all would wait out (see
        // NoTrainingSignalAtAll_CompletesAtTheFullNominalBudget below). This is the entire value
        // of this piece over the existing fixed-duration skip (see class doc comment) -- confirmed
        // working, not just structurally plausible. Loose band (+/-300ms) since the exact
        // completion point depends on legacy's own recalculation arithmetic, not an independently
        // re-derived formula.
        var expected = (int)Math.Round(VisHeader.AvtTrainingSequenceDurationMs / 1000.0 * SampleRate);
        Assert.InRange(completedAt!.Value, expected - (int)Math.Round(0.3 * SampleRate), expected + (int)Math.Round(0.3 * SampleRate));
    }

    [Fact]
    public void NoTrainingSignalAtAll_CompletesAtTheFullNominalBudget()
    {
        var silence = new double[(int)(8 * SampleRate)]; // 8s of silence, longer than the ~7.1s budget

        var machine = new AvtTrainingLockStateMachine(SampleRate);
        int? completedAt = null;
        for (var i = 0; i < silence.Length; i++)
        {
            completedAt = machine.ProcessSample(silence[i]);
            if (completedAt is not null)
            {
                break;
            }
        }

        Assert.NotNull(completedAt);

        var expected = (int)Math.Round((9.0 + VisHeader.AvtExtraHeaderDurationMs) / 1000.0 * SampleRate);
        Assert.InRange(completedAt!.Value, expected - 10, expected + 10);
    }

    /// <summary>Renders the AVT header's training portion only (VIS repeats stripped, since in
    /// production this class is only ever started once the mode is already known as AVT -- see
    /// <see cref="AnalogFmSstvDecoder"/>'s wiring), then runs it through a real
    /// <see cref="PllFmDemodulator"/> matching this port's actual decode path.</summary>
    private static double[] RenderTrainingSequenceAsDemodulatedHz(int visCode)
    {
        var allSegments = VisHeader.GenerateAvtSegments(visCode).ToArray();
        var visRepeatDurationMs = VisHeader.AvtVisRepeatCount * VisHeader.AvtVisBlockDurationMs;

        var samples = new List<float>();
        var phase = 0.0;
        var idealSamplesSoFar = 0.0;
        long emittedSamples = 0;
        var skippedMs = 0.0;
        var trainingSamples = new List<float>();

        foreach (var (frequencyHz, durationMs) in allSegments)
        {
            idealSamplesSoFar += durationMs / 1000.0 * SampleRate;
            var targetEmitted = (long)Math.Round(idealSamplesSoFar);
            var samplesToEmit = targetEmitted - emittedSamples;
            emittedSamples = targetEmitted;

            var phaseIncrement = 2 * Math.PI * frequencyHz / SampleRate;
            for (var i = 0; i < samplesToEmit; i++)
            {
                phase += phaseIncrement;
                if (phase >= 2 * Math.PI)
                {
                    phase -= 2 * Math.PI;
                }

                var sample = (float)Math.Sin(phase);
                samples.Add(sample);
                if (skippedMs >= visRepeatDurationMs)
                {
                    trainingSamples.Add(sample);
                }
            }

            skippedMs += durationMs;
        }

        // Trailing silence (matching real image data following the training sequence) -- the state
        // machine needs a little runway past the last real block to reach its own timeout-triggered
        // return, since WaitNextMarker's post-last-block phase and the (by then small) recalculated
        // overall budget both still need a few more samples to expire.
        for (var i = 0; i < SampleRate; i++)
        {
            samples.Add(0f);
            trainingSamples.Add(0f);
        }

        var demodulator = new PllFmDemodulator(SampleRate, 1100, 2300);
        // Let the demodulator settle through the VIS-repeat portion first (matching real
        // continuous operation), discarding its output, then start recording from the training
        // sequence's own first marker.
        var result = new double[trainingSamples.Count];
        var trainingStartIndex = samples.Count - trainingSamples.Count;
        for (var i = 0; i < samples.Count; i++)
        {
            var f = demodulator.ProcessSample(samples[i]);
            if (i >= trainingStartIndex)
            {
                result[i - trainingStartIndex] = f;
            }
        }

        return result;
    }
}
