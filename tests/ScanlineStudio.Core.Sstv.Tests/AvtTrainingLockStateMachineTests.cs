namespace ScanlineStudio.Core.Sstv.Tests;

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
    public void MsToSamples_BitWindowAt11025Hz_TruncatesTo107_NotRoundsTo108()
    {
        // ultracode audit finding #11: legacy assigns `9.7646 * SampFreq/1000` (107.654) directly to
        // an int field, truncating to 107, not rounding to 108.
        var machine = new AvtTrainingLockStateMachine(SampleRate);

        Assert.Equal(107, machine.MsToSamplesForTests(9.7646));
    }

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

        // Batch 6 chunk 6d fix (docs/functional-audit-playbook.md): the prior +/-300ms (+/-3308
        // sample) band was 58x too loose to actually prove a real lock happened -- a signal with
        // NO lock at all completes only ~57 samples away from a real lock's own completion point
        // (see the no-lock comparison below), well inside that old band. This is exactly how the
        // second documented bug (an earlier version overwriting _phaseCounter in the h==0x40
        // branch, a ~63-sample/~5.7ms shift) went undetected by this same test. Tight band, a real
        // golden value independently re-measured against the CURRENT code (not pasted from a prior
        // round's own claim, which was off by 26 samples) -- re-measure this value again if this
        // file's timing constants ever change deliberately.
        Assert.InRange(completedAt!.Value, 58626, 58642);

        // The actual point of this class over the fixed-duration skip (see class doc comment): a
        // real lock must finish STRICTLY EARLIER than the never-locked timeout path. Without this,
        // the tight assertion above would still pass even with DecodeBits' checksum check and the
        // per-block timeout recalculation deleted entirely -- the two outcomes are separated by
        // only ~57 samples out of a ~58600-sample budget.
        var noLockCompletionAt = (int)((9.0 + VisHeader.AvtTrainingSequenceDurationMs) / 1000.0 * SampleRate);
        Assert.True(
            completedAt!.Value < noLockCompletionAt,
            $"lock completed at {completedAt}, no-lock timeout is {noLockCompletionAt} -- recalculation may not be happening");
    }

    [Fact]
    public void NoTrainingSignalAtAll_CompletesAtTheFullNominalBudget()
    {
        var silence = new double[(int)(8 * SampleRate)]; // 8s of silence, longer than the ~5.3s budget

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

        // This class is only ever constructed once AnalogFmSstvDecoder has already skipped past all
        // 3 VIS repeats (see AvtTrainingLockStateMachine's own doc comment on why its internal budget
        // is scoped to just the training sequence's own duration, not legacy's full case-3 figure,
        // which would double-count those already-skipped repeats). ~5321ms/58667 samples here, NOT
        // legacy's own full "9 + 2xVIS-block + training" figure (~7141ms/78732 samples) -- that
        // pre-rescoping-fix number belonged to a version of this class that double-counted the two
        // VIS-repeat durations the caller already skips (Batch 6 chunk 6d correction: a stale
        // comment elsewhere in this file cited the old figure after the fix already landed).
        var expected = (int)Math.Round((9.0 + VisHeader.AvtTrainingSequenceDurationMs) / 1000.0 * SampleRate);
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

        // Piece 9 step 3: matches AnalogFmSstvDecoder's real 1500-2300Hz band (legacy's own CPLL
        // range) -- was 1100-2300Hz, a stale artifact of the pre-piece-9 PLL config this test is
        // deliberately built to track (see class doc comment: "matching this port's actual decode
        // path"). Left un-updated here, this test would have silently stayed green against a
        // config production no longer uses, defeating the point of re-measuring AVT's lock margin.
        var demodulator = new PllFmDemodulator(SampleRate, 1500, 2300);
        // Let the demodulator settle through the VIS-repeat portion first (matching real
        // continuous operation), discarding its output, then start recording from the training
        // sequence's own first marker.
        var result = new double[trainingSamples.Count];
        var trainingStartIndex = samples.Count - trainingSamples.Count;
        for (var i = 0; i < samples.Count; i++)
        {
            // Scale bridge -- see PllFmDemodulator's own doc comment. A no-op at this test's own
            // full-scale amplitude (both scaled and unscaled paths converge to the same AGC gain),
            // kept for consistency with the production call site rather than leaving a stray
            // unscaled usage that could mislead a future reader into thinking raw samples are fine.
            var f = demodulator.ProcessSample(samples[i] * 32768.0);
            if (i >= trainingStartIndex)
            {
                result[i - trainingStartIndex] = f;
            }
        }

        return result;
    }
}
