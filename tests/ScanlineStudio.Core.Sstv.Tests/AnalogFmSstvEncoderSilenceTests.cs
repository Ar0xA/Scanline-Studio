using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Tests for the true-silence representation added to <see cref="AnalogFmSstvEncoder"/>'s sample
/// loop for the CW-ID/FSK station-ID subsystem (a segment with <c>FrequencyHz == 0</c>). Before this
/// change, <c>Math.Sin(phase)</c> was called unconditionally, so a "silent" segment produced a
/// constant (DC), not silence -- see the implementation plan's Ground Truth section for the full
/// citation trail (`sstv.cpp:2865-2875`, `CSSTVMOD::Do`).
/// </summary>
public class AnalogFmSstvEncoderSilenceTests
{
    private const int SampleRate = 11025;

    [Fact]
    public void RenderSegments_AllSilence_ProducesExactlyZeroSamples()
    {
        // A fresh TxOutputBandpassFilter starts with zero internal state (matches legacy's
        // per-transmission InitTXBuf -> m_BPF.Clear() reset) -- filtering an all-zero input through
        // a linear filter with zero initial state must yield exactly zero output forever, so this
        // is an exact (not approximate) assertion, filter included.
        var samples = AnalogFmSstvEncoder.RenderSegments([(0.0, 100.0)], SampleRate).ToList();

        Assert.NotEmpty(samples);
        Assert.All(samples, s => Assert.Equal(0.0f, s));
    }

    [Fact]
    public void RenderSegments_ToneThenSilenceThenTone_UnfilteredPhaseIsContinuousAcrossTheGap()
    {
        // Legacy CSSTVMOD::Do (sstv.cpp:2865-2875) skips m_vco.Do() entirely during silence, so the
        // VCO's phase is frozen, not reset -- a tone resuming after a gap continues exactly where it
        // left off. Verified here on the RAW (unfiltered) sine, since the output bandpass filter's
        // own transient response would otherwise mask the property actually being tested.
        const double toneHz = 1000.0;
        const double toneMs = 5.0;
        const double gapMs = 3.0;

        // Round-2 code-review correction: these ms values do NOT divide evenly into whole samples
        // at SampleRate=11025 (5ms/3ms/10ms -> 55.125/33.075/110.25 samples). The test aligns
        // anyway because RenderSegments' running accumulator floors ((long) truncation) at each
        // call boundary the SAME way whether segments are split across several independent
        // RenderSegments calls or one combined call -- 55, then 88-55=33, then 143-88=55 matches
        // 55 and 33 computed standalone. If you change SampleRate/toneMs/gapMs, a boundary mismatch
        // will make this test FAIL (not silently pass) -- but you'll see an off-by-a-few-samples
        // assertion failure with no obvious cause unless you know why.

        // Reference: one unbroken tone for 2*toneMs, no gap.
        var reference = AnalogFmSstvEncoder
            .RenderSegments([(toneHz, toneMs * 2)], SampleRate, applyFilter: false)
            .ToList();

        // Same total tone duration, split by a silent gap in between.
        var withGap = AnalogFmSstvEncoder
            .RenderSegments([(toneHz, toneMs), (0.0, gapMs), (toneHz, toneMs)], SampleRate, applyFilter: false)
            .ToList();

        var firstHalfSampleCount = AnalogFmSstvEncoder
            .RenderSegments([(toneHz, toneMs)], SampleRate, applyFilter: false)
            .Count();
        var gapSampleCount = AnalogFmSstvEncoder
            .RenderSegments([(0.0, gapMs)], SampleRate, applyFilter: false)
            .Count();

        // The samples AFTER the gap in `withGap` must exactly match the samples AFTER the
        // equivalent elapsed time in the unbroken `reference` tone -- i.e. the gap contributed zero
        // phase advancement, so resuming is indistinguishable from never having paused.
        var postGapActual = withGap.Skip(firstHalfSampleCount + gapSampleCount).ToList();
        var referenceContinuation = reference.Skip(firstHalfSampleCount).ToList();

        Assert.Equal(referenceContinuation, postGapActual);

        // And the gap itself really is silence, not a phase-frozen but audible DC value.
        var gapSamples = withGap.Skip(firstHalfSampleCount).Take(gapSampleCount);
        Assert.All(gapSamples, s => Assert.Equal(0.0f, s));
    }

    [Fact]
    public void RenderSegments_ToneThenSilence_FilterRingsThenDecaysToExactZero()
    {
        // Code-review finding: the two tests above don't actually distinguish "filter runs over
        // silence" from "filter is skipped for silence" -- an all-silence input is zero-in/zero-out
        // either way. This test uses the one input that DOES distinguish them: a tone immediately
        // followed by silence. TxOutputBandpassFilter is a 24-tap FIR (TxOutputBandpassFilter.cs,
        // `Tap = 24`) with a zero-initialized delay line -- if the filter genuinely keeps processing
        // zero SAMPLES during the gap (sstv.cpp:2914's contract), the first several gap samples must
        // still be non-zero (the tone's energy still draining out of the delay line), and only once
        // Tap+1 zero samples have fully flushed the line does the output become exactly zero. If an
        // implementation instead special-cased silence by skipping the filter call entirely, EVERY
        // gap sample would be zero immediately -- indistinguishable from this test failing.
        const double toneHz = 1000.0;
        // Options stub backlog item 3: tap count is now a real (variable) constructor parameter, not
        // a compile-time constant -- reference the real default instead of a hand-kept-in-sync magic
        // number, which is no longer even accurate once a caller passes a non-default tap count.
        const int tapCount = TxOutputBandpassFilter.DefaultTapCount;

        var samples = AnalogFmSstvEncoder
            .RenderSegments([(toneHz, 20.0), (0.0, 20.0)], SampleRate)
            .ToList();
        var toneSampleCount = AnalogFmSstvEncoder.RenderSegments([(toneHz, 20.0)], SampleRate).Count();
        var gapSamples = samples.Skip(toneSampleCount).ToList();

        Assert.True(gapSamples.Count > tapCount + 1, "Gap too short to observe both ringing and decay.");
        Assert.Contains(gapSamples.Take(tapCount), s => s != 0.0f);

        var afterFullFlush = gapSamples.Skip(tapCount + 1);
        Assert.All(afterFullFlush, s => Assert.Equal(0.0f, s));
    }

    [Fact]
    public void RenderSegments_NonSilentSegment_StillAppliesTheOutputBandpassFilter()
    {
        // Guard against a naive implementation that special-cases silence by skipping the filter
        // call entirely for the WHOLE segment stream -- legacy runs the filter over every sample,
        // silence included (sstv.cpp:2914). This just confirms filtered output differs from the raw
        // sine for a non-silent segment (i.e. the filter is genuinely still in the loop).
        var filtered = AnalogFmSstvEncoder.RenderSegments([(1000.0, 10.0)], SampleRate).ToList();
        var raw = AnalogFmSstvEncoder.RenderSegments([(1000.0, 10.0)], SampleRate, applyFilter: false).ToList();

        Assert.NotEqual(raw, filtered);
    }

    // CW-ID "TEST" at 28 WPM: one dot is ~437 samples (1110/WPM ms), so a 100-sample run of exact zeros is a real gap.
    private const int MinGapSamples = 100;
    private const double CwToneHz = 1000.0;

    private static readonly StationIdTransmitOptions CwId =
        new() { CwEnabled = true, CwResolvedText = "TEST", CwToneFrequencyHz = CwToneHz, CwWpm = 28 };

    [Fact]
    public async Task EncodeBatchedAsync_CwIdTail_BpfOff_GapsAreExactZeroAndToneResumesInPhase()
    {
        var tail = await EncodeCwTailAsync(txBpfEnabled: false);
        var gaps = FindZeroRuns(tail, MinGapSamples);

        // Leading '@' silence, the gap between letters, and the gaps inside 'S'.
        Assert.True(gaps.Count >= 5, $"Expected at least 5 CW gaps, found {gaps.Count}.");

        // A phase reset to 0 during silence would make the first resumed sample exactly this value.
        var restartFromPhaseZero = (float)Math.Sin(2 * Math.PI * CwToneHz / SampleRate);
        var checkedGaps = 0;
        foreach (var (start, length) in gaps)
        {
            var resumeIndex = start + length;
            if (resumeIndex < tail.Length)
            {
                Assert.NotEqual(restartFromPhaseZero, tail[resumeIndex]);
                checkedGaps++;
            }
        }

        Assert.True(checkedGaps >= 4, $"Expected at least 4 gaps followed by a tone, found {checkedGaps}.");
    }

    [Fact]
    public async Task EncodeBatchedAsync_CwIdTail_BpfOn_FirstGapRingsDownThenIsExactZero()
    {
        const int tapCount = TxOutputBandpassFilter.DefaultTapCount;
        var tailBpfOff = await EncodeCwTailAsync(txBpfEnabled: false);
        var tailBpfOn = await EncodeCwTailAsync(txBpfEnabled: true);
        Assert.Equal(tailBpfOff.Length, tailBpfOn.Length);

        var (start, length) = FindZeroRuns(tailBpfOff, MinGapSamples)[0];
        var gap = tailBpfOn.Skip(start).Take(length).ToArray();

        Assert.True(gap.Length > tapCount + 1, "Gap too short to observe both ringing and decay.");
        Assert.Contains(gap.Take(tapCount), s => s != 0.0f);
        Assert.All(gap.Skip(tapCount + 1), s => Assert.Equal(0.0f, s));
    }

    [Fact]
    public async Task EncodeBatchedAsync_BpfOnVersusOff_TonesDiffer()
    {
        var mode = SstvModeRegistry.Robot36;
        var image = AnalogFmSstvEncoderLoopParityTests.CreatePatternImage(mode.ImageWidth, mode.ImageHeight);
        var encoder = new AnalogFmSstvEncoder(SampleRate);

        var bpfOn = await CollectBatchesAsync(encoder.EncodeBatchedAsync(mode, image, txBpfEnabled: true));
        var bpfOff = await CollectBatchesAsync(encoder.EncodeBatchedAsync(mode, image, txBpfEnabled: false));

        Assert.Equal(bpfOff.Length, bpfOn.Length);
        Assert.NotEqual(bpfOff, bpfOn);
    }

    /// <summary>Returns the CW-ID tail of a real robot-36 encode: everything after the
    /// without-CW output length (same pattern as <c>AnalogFmSstvEncoderStationIdWiringTests</c>).</summary>
    private static async Task<float[]> EncodeCwTailAsync(bool txBpfEnabled)
    {
        var mode = SstvModeRegistry.Robot36;
        var image = AnalogFmSstvEncoderLoopParityTests.CreatePatternImage(mode.ImageWidth, mode.ImageHeight);
        var encoder = new AnalogFmSstvEncoder(SampleRate);

        var withoutCw = await CollectBatchesAsync(encoder.EncodeBatchedAsync(mode, image, txBpfEnabled: txBpfEnabled));
        var withCw = await CollectBatchesAsync(encoder.EncodeBatchedAsync(mode, image, CwId, txBpfEnabled: txBpfEnabled));

        Assert.True(withCw.Length > withoutCw.Length);
        return withCw[withoutCw.Length..];
    }

    private static List<(int Start, int Length)> FindZeroRuns(float[] samples, int minLength)
    {
        var runs = new List<(int Start, int Length)>();
        var i = 0;
        while (i < samples.Length)
        {
            if (BitConverter.SingleToInt32Bits(samples[i]) != 0)
            {
                i++;
                continue;
            }

            var start = i;
            while (i < samples.Length && BitConverter.SingleToInt32Bits(samples[i]) == 0)
            {
                i++;
            }

            if (i - start >= minLength)
            {
                runs.Add((start, i - start));
            }
        }

        return runs;
    }

    private static async Task<float[]> CollectBatchesAsync(IAsyncEnumerable<ReadOnlyMemory<float>> batches)
    {
        var list = new List<float>();
        await foreach (var batch in batches)
        {
            list.AddRange(batch.ToArray());
        }

        return list.ToArray();
    }
}
