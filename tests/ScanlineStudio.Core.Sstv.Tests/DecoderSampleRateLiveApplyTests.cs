using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Restart-required-settings backlog item 4 (2026-08-27): sample rate is now live-settable via
/// <see cref="RestartableSstvDecoder.RequestSampleRate"/>/<see cref="RestartableSstvDecoder.ApplyPendingReconfigurationNow"/>
/// -- deliberately NOT drained by the push-driven idle-swap path
/// <see cref="DecoderReconfigurationLiveApplyTests"/> covers for RxBpfPreset/DemodType/RxBufferMode,
/// since the decoder cannot know whether incoming audio is still physically sampled at the old rate.
/// Five plan-review rounds found and fixed: an inverted commit order, a merge design that let one
/// Options field silently erase another's pending change, an unbounded rebuild-and-rearm loop for a
/// queued-but-undrained rate remainder, a dropped remainder on an unrelated sibling failure, and an
/// undefined orchestrator outcome for a rejected (non-throwing) swap attempt.
/// </summary>
public class DecoderSampleRateLiveApplyTests
{
    private const int InitialSampleRate = 11025;
    private const int NewSampleRate = 8000;

    [Fact]
    public void RequestSampleRate_QueuedButNotApplied_PushDrivenIdlePathNeverDrainsIt()
    {
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 1_000_000, criticalThresholdSamples: 10_000_000,
            sampleRate: InitialSampleRate);

        decoder.RequestSampleRate(NewSampleRate);

        // Several idle pushes must NOT self-swap -- only ApplyPendingReconfigurationNow ever drains
        // a rate-only remainder (round-4 finding B1: the predicate excludes it precisely to prevent
        // an unbounded rebuild-and-rearm loop here).
        for (var i = 0; i < 5; i++)
        {
            decoder.PushSamples(new float[8]);
        }

        Assert.Equal(0, decoder.RestartCountForTests);
        Assert.Equal(InitialSampleRate, decoder.SampleRate);
    }

    [Fact]
    public void ApplyPendingReconfigurationNow_RateOnly_Commits()
    {
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 1_000_000, criticalThresholdSamples: 10_000_000,
            sampleRate: InitialSampleRate);

        decoder.RequestSampleRate(NewSampleRate);
        var result = decoder.ApplyPendingReconfigurationNow();

        Assert.Equal(SwapResult.Committed, result);
        Assert.Equal(NewSampleRate, decoder.SampleRate);
        Assert.Equal(1, decoder.RestartCountForTests);
    }

    [Fact]
    public void ApplyPendingReconfigurationNow_RateAndSiblingBothQueued_AppliesBothInOneSwap()
    {
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 1_000_000, criticalThresholdSamples: 10_000_000,
            sampleRate: InitialSampleRate, demodType: DemodType.Hilbert, rxBpfPreset: RxBpfPreset.Wide, rxBufferMode: RxBufferMode.On);

        decoder.RequestSampleRate(NewSampleRate);
        decoder.RequestReconfiguration(RxBpfPreset.Narrow, DemodType.Pll, RxBufferMode.Off);

        var result = decoder.ApplyPendingReconfigurationNow();

        Assert.Equal(SwapResult.Committed, result);
        Assert.Equal(1, decoder.RestartCountForTests); // one swap, not two
        Assert.Equal(NewSampleRate, decoder.SampleRate);
        Assert.Equal(RxBpfPreset.Narrow, decoder.InnerRxBpfPresetForTests);
        Assert.Equal(DemodType.Pll, decoder.InnerDemodTypeForTests);
        Assert.Equal(RxBufferMode.Off, decoder.InnerRxBufferModeForTests);
    }

    [Theory]
    [InlineData(true)] // rate requested first, then sibling
    [InlineData(false)] // sibling requested first, then rate
    public void QueuingRateAndSiblingFromSeparateCalls_NeitherErasesTheOther(bool rateFirst)
    {
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 1_000_000, criticalThresholdSamples: 10_000_000,
            sampleRate: InitialSampleRate, demodType: DemodType.Hilbert, rxBpfPreset: RxBpfPreset.Wide, rxBufferMode: RxBufferMode.On);

        if (rateFirst)
        {
            decoder.RequestSampleRate(NewSampleRate);
            decoder.RequestReconfiguration(RxBpfPreset.Narrow, DemodType.Pll, RxBufferMode.Off);
        }
        else
        {
            decoder.RequestReconfiguration(RxBpfPreset.Narrow, DemodType.Pll, RxBufferMode.Off);
            decoder.RequestSampleRate(NewSampleRate);
        }

        var result = decoder.ApplyPendingReconfigurationNow();

        Assert.Equal(SwapResult.Committed, result);
        Assert.Equal(NewSampleRate, decoder.SampleRate);
        Assert.Equal(RxBpfPreset.Narrow, decoder.InnerRxBpfPresetForTests);
    }

    [Fact]
    public void RequestReconfiguration_NoOpSiblingCall_DoesNotEraseSeparatelyQueuedRate()
    {
        // Round-2 finding B3/B2's original bug: RequestReconfiguration's own no-op guard used to
        // replace the WHOLE pending record, silently wiping a separately-queued sample-rate change.
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 1_000_000, criticalThresholdSamples: 10_000_000,
            sampleRate: InitialSampleRate, demodType: DemodType.Hilbert, rxBpfPreset: RxBpfPreset.Wide, rxBufferMode: RxBufferMode.On);

        decoder.RequestSampleRate(NewSampleRate);
        decoder.RequestReconfiguration(RxBpfPreset.Wide, DemodType.Hilbert, RxBufferMode.On); // no-op vs. current values

        var result = decoder.ApplyPendingReconfigurationNow();

        Assert.Equal(SwapResult.Committed, result);
        Assert.Equal(NewSampleRate, decoder.SampleRate); // survived the sibling no-op call
    }

    [Fact]
    public void RequestSampleRate_ThenRequestRxBpfPreset_NeitherErasesTheOther()
    {
        // Code-review round-1 finding (RX BPF Receive-tab live dropdown, 2026-08-28):
        // RequestRxBpfPreset's own `preservedSampleRate` line had no test combining the two --
        // mirrors QueuingRateAndSiblingFromSeparateCalls_NeitherErasesTheOther above, but through the
        // new per-field entry point instead of RequestReconfiguration.
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 1_000_000, criticalThresholdSamples: 10_000_000,
            sampleRate: InitialSampleRate, demodType: DemodType.Hilbert, rxBpfPreset: RxBpfPreset.Wide, rxBufferMode: RxBufferMode.On);

        // Order matters for this test's own coverage claim: RequestSampleRate must come FIRST so the
        // pending record already has a SampleRate queued before RequestRxBpfPreset runs -- only then
        // does RequestRxBpfPreset's own `preservedSampleRate` read actually get exercised (the
        // reverse order would let RequestSampleRate's own unconditional field write mask a broken
        // preserve in RequestRxBpfPreset, since it runs second and always wins that field).
        decoder.RequestSampleRate(NewSampleRate);
        decoder.RequestRxBpfPreset(RxBpfPreset.Narrow);

        var result = decoder.ApplyPendingReconfigurationNow();

        Assert.Equal(SwapResult.Committed, result);
        Assert.Equal(NewSampleRate, decoder.SampleRate);
        Assert.Equal(RxBpfPreset.Narrow, decoder.InnerRxBpfPresetForTests);
    }

    [Fact]
    public void RequestRxBpfPreset_NoOpCall_DoesNotEraseSeparatelyQueuedRate()
    {
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 1_000_000, criticalThresholdSamples: 10_000_000,
            sampleRate: InitialSampleRate, demodType: DemodType.Hilbert, rxBpfPreset: RxBpfPreset.Wide, rxBufferMode: RxBufferMode.On);

        decoder.RequestSampleRate(NewSampleRate);
        decoder.RequestRxBpfPreset(RxBpfPreset.Wide); // no-op vs. current value

        var result = decoder.ApplyPendingReconfigurationNow();

        Assert.Equal(SwapResult.Committed, result);
        Assert.Equal(NewSampleRate, decoder.SampleRate); // survived the sibling no-op call
    }

    [Fact]
    public void RequestSampleRate_NoOpSiblingRequestClearedByReRequestingCurrentRate_DoesNotEraseSiblingChange()
    {
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 1_000_000, criticalThresholdSamples: 10_000_000,
            sampleRate: InitialSampleRate, demodType: DemodType.Hilbert, rxBpfPreset: RxBpfPreset.Wide, rxBufferMode: RxBufferMode.On);

        decoder.RequestReconfiguration(RxBpfPreset.Narrow, DemodType.Pll, RxBufferMode.Off);
        decoder.RequestSampleRate(InitialSampleRate); // no-op vs. current rate (the Busy-recovery shape)

        var result = decoder.ApplyPendingReconfigurationNow();

        Assert.Equal(SwapResult.Committed, result);
        Assert.Equal(InitialSampleRate, decoder.SampleRate);
        Assert.Equal(RxBpfPreset.Narrow, decoder.InnerRxBpfPresetForTests); // survived the rate no-op
    }

    [Fact]
    public void ApplyPendingReconfigurationNow_ThresholdsRecomputedForNewRate()
    {
        // Test-seam interaction, documented in the implementation plan: this constructor overload
        // injects short test thresholds (1,000,000/10,000,000) for the INITIAL rate, but a
        // rate-carrying swap always recomputes via the production ComputeDefaultThresholds formula
        // for the NEW rate -- so after this swap, the thresholds jump to production (many-hour)
        // values. Verified directly via ThresholdsForTests rather than by driving a real push up to
        // a production-sized sample count (hundreds of millions of samples -- not a realistic test
        // allocation).
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 1_000_000, criticalThresholdSamples: 10_000_000,
            sampleRate: InitialSampleRate);

        var initialThresholds = decoder.ThresholdsForTests;
        Assert.Equal(1_000_000, initialThresholds.WarningThresholdSamples);

        decoder.RequestSampleRate(NewSampleRate);
        decoder.ApplyPendingReconfigurationNow();

        var expected = RestartableSstvDecoder.ComputeDefaultThresholds(NewSampleRate);
        var actual = decoder.ThresholdsForTests;

        Assert.Equal(expected.WarningThresholdSamples, actual.WarningThresholdSamples);
        Assert.Equal(expected.CriticalThresholdSamples, actual.CriticalThresholdSamples);
        Assert.Equal(expected.MaximumSafeSampleIndex, actual.MaximumSafeSampleIndex);
    }

    [Fact]
    public void ApplyPendingReconfigurationNow_CreateInnerFails_RollsBackAllFourRateFields()
    {
        var callCount = 0;
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 1_000_000, criticalThresholdSamples: 10_000_000,
            sampleRate: InitialSampleRate,
            decoderFactoryForTests: sr =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return new AnalogFmSstvDecoder(sampleRate: sr); // the wrapper's own initial construction
                }

                throw new InvalidOperationException("Simulated CreateInner failure for the pending rate.");
            });

        decoder.RequestSampleRate(NewSampleRate);
        var result = decoder.ApplyPendingReconfigurationNow();

        Assert.Equal(SwapResult.Rejected, result);
        Assert.Equal(InitialSampleRate, decoder.SampleRate); // rolled back, not left half-mutated
        Assert.Equal(0, decoder.RestartCountForTests);

        var thresholds = decoder.ThresholdsForTests;
        Assert.Equal(1_000_000, thresholds.WarningThresholdSamples); // the injected test thresholds, not NewSampleRate's -- rolled back too
        Assert.Equal(2, callCount); // 1 for construction + exactly 1 failed attempt, no retry
    }

    [Fact]
    public void RequestSampleRate_Unsupported_ThrowsImmediately_NoFieldMutated()
    {
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 1_000_000, criticalThresholdSamples: 10_000_000,
            sampleRate: InitialSampleRate);

        Assert.Throws<ArgumentOutOfRangeException>(() => decoder.RequestSampleRate(SstvSampleRate.Maximum + 1));

        var result = decoder.ApplyPendingReconfigurationNow();
        Assert.Equal(SwapResult.NothingPending, result); // nothing was ever queued
        Assert.Equal(InitialSampleRate, decoder.SampleRate);
    }

    [Fact]
    public void ApplyPendingReconfigurationNow_PushGenuinelyInFlight_ReturnsBusy_NothingChanges()
    {
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 1_000_000, criticalThresholdSamples: 10_000_000,
            sampleRate: InitialSampleRate);

        decoder.RequestSampleRate(NewSampleRate);
        decoder.SetPushActiveForTests(active: true);
        try
        {
            var result = decoder.ApplyPendingReconfigurationNow();

            Assert.Equal(SwapResult.Busy, result);
            Assert.Equal(InitialSampleRate, decoder.SampleRate); // untouched
            Assert.Equal(0, decoder.RestartCountForTests);
        }
        finally
        {
            decoder.SetPushActiveForTests(active: false);
        }

        // The pending rate survives a Busy outcome (the caller, not the decoder, decides whether to
        // clear it -- see SstvSessionService's own Busy-recovery sequence in the implementation plan).
        var committed = decoder.ApplyPendingReconfigurationNow();
        Assert.Equal(SwapResult.Committed, committed);
        Assert.Equal(NewSampleRate, decoder.SampleRate);
    }

    [Fact]
    public void SampleRate_Getter_ReflectsCommittedValueImmediatelyAfterSwap()
    {
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 1_000_000, criticalThresholdSamples: 10_000_000,
            sampleRate: InitialSampleRate);

        Assert.Equal(InitialSampleRate, decoder.SampleRate);

        decoder.RequestSampleRate(NewSampleRate);
        decoder.ApplyPendingReconfigurationNow();

        Assert.Equal(NewSampleRate, decoder.SampleRate);
    }

    [Fact]
    public void MandatoryOverflowSwap_WithARateOnlyPendingRequest_NeverDrainsTheRate_RemainderSurvives()
    {
        // Code-review round-1 gap (round-4 finding C1's own core guarantee, previously untested): a
        // MANDATORY push-driven swap (overflow/critical/12h-warning) must never drain a queued rate,
        // since capture could still be physically streaming at the OLD hardware rate when it fires --
        // only the direct ApplyPendingReconfigurationNow call (after the orchestrator stops capture)
        // may ever commit one. maximumSafeSampleIndex forces the overflow branch deterministically.
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 1_000_000, criticalThresholdSamples: 10_000_000,
            maximumSafeSampleIndex: 10, sampleRate: InitialSampleRate);

        decoder.PushSamples(new float[5]); // n=5, no pending yet
        decoder.RequestSampleRate(NewSampleRate);

        // n(5) + this chunk(6) > maximumSafeSampleIndex(10) -- forces the mandatory overflow branch.
        decoder.PushSamples(new float[6]);

        Assert.Equal(1, decoder.RestartCountForTests); // the mandatory swap DID commit (a rebuild happened)
        Assert.Equal(InitialSampleRate, decoder.SampleRate); // but the rate was NOT drained by it

        // The remainder must have survived the mandatory swap, not been dropped -- a direct commit
        // still applies it afterward.
        var result = decoder.ApplyPendingReconfigurationNow();
        Assert.Equal(SwapResult.Committed, result);
        Assert.Equal(NewSampleRate, decoder.SampleRate);
        Assert.Equal(2, decoder.RestartCountForTests); // a SECOND, separate swap for the rate commit
    }
}
