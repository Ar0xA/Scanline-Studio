using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Restart-required-settings backlog item 2 (2026-08-27): RxBpfPreset/DemodType/RxBufferMode are now
/// live-settable via <see cref="RestartableSstvDecoder.RequestReconfiguration"/> -- architecturally
/// different from item 1's four booleans (<see cref="DecoderFlagsLiveApplyTests"/>): none of these
/// three has any in-place mutation path on <see cref="AnalogFmSstvDecoder"/> (each is
/// <c>readonly</c> there, read once at construction), so a requested change only ever applies via a
/// full idle-gated instance swap, reusing <see cref="RestartableSstvDecoder"/>'s existing periodic-
/// maintenance machinery. Three plan-review rounds found and fixed: a failure-ordering bug (a
/// throwing <c>CreateInner</c> during the drain used to leave the wrapper's committed fields
/// permanently wrong), an infinite-retry lockout (a PERSISTENT <c>CreateInner</c> failure used to
/// re-arm the same idle-drain trigger forever), and an incorrect "no live-safe path exists" framing
/// for 2 of the 3 settings (legacy actually does apply RxBpfPreset/DemodType live -- this port
/// idle-gates as a documented, deliberate divergence instead, not a forced necessity).
/// </summary>
public class DecoderReconfigurationLiveApplyTests
{
    [Fact]
    public void RequestReconfiguration_EqualToCurrentValues_ClearsAnyPendingAndNeverSwaps()
    {
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 1_000_000, criticalThresholdSamples: 10_000_000,
            demodType: DemodType.Hilbert, rxBpfPreset: RxBpfPreset.Wide, rxBufferMode: RxBufferMode.On);

        decoder.RequestReconfiguration(RxBpfPreset.Wide, DemodType.Hilbert, RxBufferMode.On);
        decoder.PushSamples(new float[8]); // idle -- would swap if anything were actually pending

        Assert.Equal(0, decoder.RestartCountForTests);
    }

    [Fact]
    public void RequestReconfiguration_QueuesAndAppliesOnNextIdlePush()
    {
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 1_000_000, criticalThresholdSamples: 10_000_000,
            demodType: DemodType.Hilbert, rxBpfPreset: RxBpfPreset.Wide, rxBufferMode: RxBufferMode.On);

        decoder.RequestReconfiguration(RxBpfPreset.Narrow, DemodType.Pll, RxBufferMode.Off);
        decoder.PushSamples(new float[8]); // idle, pending queued -- the ONLY discretionary swap trigger

        Assert.Equal(1, decoder.RestartCountForTests);
        Assert.Equal(RxBpfPreset.Narrow, decoder.RxBpfPreset);
        Assert.Equal(RxBpfPreset.Narrow, decoder.InnerRxBpfPresetForTests);
        Assert.Equal(DemodType.Pll, decoder.InnerDemodTypeForTests);
        Assert.Equal(RxBufferMode.Off, decoder.InnerRxBufferModeForTests);
    }

    [Fact]
    public void RequestReconfiguration_QueueThenRevertBeforeIdlePush_CancelsPendingRequest()
    {
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 1_000_000, criticalThresholdSamples: 10_000_000,
            demodType: DemodType.Hilbert, rxBpfPreset: RxBpfPreset.Wide, rxBufferMode: RxBufferMode.On);

        decoder.RequestReconfiguration(RxBpfPreset.Narrow, DemodType.Pll, RxBufferMode.Off);
        decoder.RequestReconfiguration(RxBpfPreset.Wide, DemodType.Hilbert, RxBufferMode.On); // reverts to the currently-committed values

        decoder.PushSamples(new float[8]);

        Assert.Equal(0, decoder.RestartCountForTests);
    }

    [Fact]
    public void RequestReconfiguration_DoubleRequestBeforeIdlePush_OnlyLatestApplies_OneSwap()
    {
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 1_000_000, criticalThresholdSamples: 10_000_000,
            demodType: DemodType.Hilbert, rxBpfPreset: RxBpfPreset.Wide, rxBufferMode: RxBufferMode.On);

        decoder.RequestReconfiguration(RxBpfPreset.Narrow, DemodType.Pll, RxBufferMode.Off);
        decoder.RequestReconfiguration(RxBpfPreset.VeryNarrow, DemodType.ZeroCrossing, RxBufferMode.Extended);

        decoder.PushSamples(new float[8]);

        Assert.Equal(1, decoder.RestartCountForTests);
        Assert.Equal(RxBpfPreset.VeryNarrow, decoder.InnerRxBpfPresetForTests);
        Assert.Equal(DemodType.ZeroCrossing, decoder.InnerDemodTypeForTests);
        Assert.Equal(RxBufferMode.Extended, decoder.InnerRxBufferModeForTests);
    }

    [Fact]
    public async Task RequestReconfiguration_NotIdleMidLockedReception_DoesNotApplyUntilReceptionEnds()
    {
        // The idle gate is the whole point of this feature (round-1 plan-review blocker #2's
        // rejected alternative was applying live mid-reception) -- this proves it actually holds
        // during a REAL locked reception, not just against plain silence.
        const int sampleRate = 11025;
        var mode = SstvModeRegistry.MartinM1;
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);

        var encoder = new AnalogFmSstvEncoder(sampleRate);
        var transmissionSamples = new List<float>();
        await CollectAsync(encoder.EncodeAsync(mode, sourceImage), transmissionSamples);

        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 1_000_000, criticalThresholdSamples: 10_000_000,
            sampleRate: sampleRate, demodType: DemodType.Hilbert, rxBpfPreset: RxBpfPreset.Wide, rxBufferMode: RxBufferMode.On);

        SstvModeDefinition? detectedMode = null;
        decoder.ModeDetected += m => detectedMode = m;

        const int chunkSize = 500;
        var fed = 0;
        for (; fed < transmissionSamples.Count && detectedMode is null; fed += chunkSize)
        {
            var length = Math.Min(chunkSize, transmissionSamples.Count - fed);
            decoder.PushSamples(transmissionSamples.GetRange(fed, length).ToArray());
        }

        Assert.NotNull(detectedMode); // sanity: actually locked before requesting a change

        decoder.RequestReconfiguration(RxBpfPreset.Narrow, DemodType.Pll, RxBufferMode.Off);

        // Feed a few more chunks while still mid-image -- must NOT swap while locked.
        for (var i = 0; i < 5 && fed < transmissionSamples.Count; i++, fed += chunkSize)
        {
            var length = Math.Min(chunkSize, transmissionSamples.Count - fed);
            decoder.PushSamples(transmissionSamples.GetRange(fed, length).ToArray());
        }

        Assert.Equal(0, decoder.RestartCountForTests);
        Assert.Equal(RxBpfPreset.Wide, decoder.InnerRxBpfPresetForTests); // still the ORIGINAL value -- not applied yet

        // Finish feeding the whole transmission so the reception completes and the decoder goes idle again.
        for (; fed < transmissionSamples.Count; fed += chunkSize)
        {
            var length = Math.Min(chunkSize, transmissionSamples.Count - fed);
            decoder.PushSamples(transmissionSamples.GetRange(fed, length).ToArray());
        }

        decoder.PushSamples(new float[8]); // one more idle push -- now applies

        Assert.Equal(1, decoder.RestartCountForTests);
        Assert.Equal(RxBpfPreset.Narrow, decoder.InnerRxBpfPresetForTests);
    }

    [Fact]
    public void CreateInnerThrowsPersistently_NonMandatorySwap_RejectsWithoutThrowing_NeverRetriesAgain()
    {
        // Round-2/round-3 plan-review B1 regression: a PERSISTENT CreateInner failure (e.g.
        // RxBufferMode.Extended's scratch-file creation failing on a full/read-only temp directory)
        // must reject ONCE, not re-arm the same idle-drain trigger on every subsequent push forever.
        var callCount = 0;
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 1_000_000, criticalThresholdSamples: 10_000_000,
            decoderFactoryForTests: sr =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return new AnalogFmSstvDecoder(sampleRate: sr); // the wrapper's own initial construction
                }

                throw new InvalidOperationException("Simulated persistent CreateInner failure.");
            });

        decoder.RequestReconfiguration(RxBpfPreset.Narrow, DemodType.Pll, RxBufferMode.Off);

        var rejectedCount = 0;
        decoder.ReconfigurationRejected += () => rejectedCount++;
        // Code-review finding: RestartCountForTests staying 0 alone does NOT prove Restarted didn't
        // fire -- that counter increments only inside Swap's own commit tail, independent of whatever
        // PushSamplesCore does with Swap's returned bool. A regression that ignored the return value
        // and raised Restarted unconditionally would slip past every other assertion here.
        var restartedCount = 0;
        decoder.Restarted += () => restartedCount++;

        var ex = Record.Exception(() => decoder.PushSamples(new float[8]));
        Assert.Null(ex); // a rejected NON-mandatory swap must not throw out of PushSamples
        Assert.Equal(1, rejectedCount);
        Assert.Equal(0, restartedCount); // a rejected swap must not also spuriously fire Restarted
        Assert.Equal(0, decoder.RestartCountForTests);
        Assert.Equal(2, callCount); // 1 for construction + exactly 1 failed attempt

        // Several more idle pushes must NOT retry -- the pending marker was cleared, not left set.
        for (var i = 0; i < 5; i++)
        {
            decoder.PushSamples(new float[8]);
        }

        Assert.Equal(2, callCount);
        Assert.Equal(0, decoder.RestartCountForTests);
        Assert.Equal(1, rejectedCount); // reported exactly once, not once per subsequent push
    }

    [Fact]
    public void CreateInnerThrowsOnce_MandatorySwap_StillCompletesUsingPreviousValues_AndReportsRejection()
    {
        // Round-3 plan-review Q1/Q2/Q3: a mandatory swap (the overflow-safety guarantee itself) must
        // still commit -- using the PREVIOUSLY-COMMITTED (rolled-back) values, not the failed pending
        // ones -- and report the drop via ReconfigurationRejected rather than silently degrading with
        // no signal at all. maximumSafeSampleIndex forces the OVERFLOW branch (mandatory, no idle
        // requirement), so the swap fires deterministically without needing a real locked reception.
        var callCount = 0;
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 1_000_000, criticalThresholdSamples: 10_000_000,
            maximumSafeSampleIndex: 10,
            demodType: DemodType.Hilbert, rxBpfPreset: RxBpfPreset.Wide, rxBufferMode: RxBufferMode.On,
            decoderFactoryForTests: sr =>
            {
                callCount++;
                if (callCount == 2)
                {
                    throw new InvalidOperationException("Simulated one-shot CreateInner failure for the pending (new) values.");
                }

                return new AnalogFmSstvDecoder(sampleRate: sr);
            });

        // Build up n WITHOUT any pending change yet (a pending change would otherwise trigger the
        // discretionary idle-swap branch on the very next push, before n ever reaches the overflow
        // threshold this test needs to exercise instead).
        decoder.PushSamples(new float[5]);

        decoder.RequestReconfiguration(RxBpfPreset.Narrow, DemodType.Pll, RxBufferMode.Off);

        var rejectedCount = 0;
        decoder.ReconfigurationRejected += () => rejectedCount++;
        var restartedCount = 0;
        decoder.Restarted += () => restartedCount++;

        // n=5 before this push; maximumSafeSampleIndex(10) - n(5) = 5 < this chunk's length(6) -- forces
        // the mandatory overflow branch regardless of idle state.
        decoder.PushSamples(new float[6]);

        Assert.Equal(1, decoder.RestartCountForTests); // the mandatory swap DID commit
        Assert.Equal(1, restartedCount);
        Assert.Equal(1, rejectedCount); // ...but reports the reconfiguration was rejected
        // These three merely reflect decoderFactoryForTests' OWN hardcoded defaults (CreateInner
        // short-circuits via `_decoderFactoryForTests?.Invoke(...)` before ever constructing a real
        // AnalogFmSstvDecoder from the wrapper's fields) -- they'd pass even if the rollback were
        // deleted entirely. Kept as a sanity check, not proof.
        Assert.Equal(RxBpfPreset.Wide, decoder.InnerRxBpfPresetForTests);
        Assert.Equal(DemodType.Hilbert, decoder.InnerDemodTypeForTests);
        Assert.Equal(RxBufferMode.On, decoder.InnerRxBufferModeForTests);

        // Code-review finding: the three asserts above can't actually distinguish a real rollback
        // from the test factory's own coincidentally-matching defaults. Pin it via BEHAVIOR instead --
        // re-requesting exactly what SHOULD already be committed (Wide/Hilbert/On) must be a no-op (no
        // new swap). If the rollback had NOT happened (fields wrongly still Narrow/Pll/Off), this
        // request would differ from what's actually committed and queue a real change, which the next
        // idle push (factory call 4, past the one-shot failure at call 2) would successfully apply.
        decoder.RequestReconfiguration(RxBpfPreset.Wide, DemodType.Hilbert, RxBufferMode.On);
        decoder.PushSamples(new float[3]);
        Assert.Equal(1, decoder.RestartCountForTests); // still just the one swap -- the "revert" request was correctly a no-op
    }

    [Fact]
    public void MandatorySwap_WithASucceedingPendingReconfiguration_AppliesInTheSameSwap_NotTwo()
    {
        var decoder = new RestartableSstvDecoder(afcEnabled: true, warningThresholdSamples: 1_000_000, criticalThresholdSamples: 10_000_000,
            maximumSafeSampleIndex: 10,
            demodType: DemodType.Hilbert, rxBpfPreset: RxBpfPreset.Wide, rxBufferMode: RxBufferMode.On);

        decoder.PushSamples(new float[5]); // n=5, no pending yet -- avoids the discretionary idle-swap branch firing early
        decoder.RequestReconfiguration(RxBpfPreset.Narrow, DemodType.Pll, RxBufferMode.Off);

        decoder.PushSamples(new float[6]); // forces the mandatory overflow branch, which also drains the pending change

        Assert.Equal(1, decoder.RestartCountForTests); // exactly one swap, not one for overflow and a second for reconfiguration
        Assert.Equal(RxBpfPreset.Narrow, decoder.InnerRxBpfPresetForTests);
        Assert.Equal(DemodType.Pll, decoder.InnerDemodTypeForTests);
        Assert.Equal(RxBufferMode.Off, decoder.InnerRxBufferModeForTests);
    }

    private static async Task CollectAsync(IAsyncEnumerable<float> samples, List<float> destination)
    {
        await foreach (var sample in samples)
        {
            destination.Add(sample);
        }
    }

    private static ArrayImageSource CreateGradientTestImage(int width, int height)
    {
        var pixels = new Rgb24[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = new Rgb24(
                    R: (byte)(x * 255 / Math.Max(1, width - 1)),
                    G: (byte)(y * 255 / Math.Max(1, height - 1)),
                    B: 128);
            }
        }

        return new ArrayImageSource(width, height, pixels);
    }
}
