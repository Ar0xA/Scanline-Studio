using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

public sealed class WaterfallSourceTests
{
    [Fact]
    public void PushSamples_FewerThanOneWindow_EmitsNoFrame()
    {
        using var source = new WaterfallSource(sampleRate: 8000, windowSize: 64);
        var frames = new List<WaterfallFrame>();
        source.Frames.Subscribe(frames.Add);

        source.PushSamples(new float[32]);

        Assert.Empty(frames);
    }

    [Fact]
    public void PushSamples_ExactlyOneWindow_EmitsExactlyOneFrame()
    {
        using var source = new WaterfallSource(sampleRate: 8000, windowSize: 64);
        var frames = new List<WaterfallFrame>();
        source.Frames.Subscribe(frames.Add);

        source.PushSamples(new float[64]);

        Assert.Single(frames);
        Assert.Equal(32, frames[0].MagnitudesDb.Count);
    }

    [Fact]
    public void PushSamples_AcrossMultipleSmallChunks_StillFillsWindowAndEmits()
    {
        using var source = new WaterfallSource(sampleRate: 8000, windowSize: 64);
        var frames = new List<WaterfallFrame>();
        source.Frames.Subscribe(frames.Add);

        for (var i = 0; i < 8; i++)
        {
            source.PushSamples(new float[8]);
        }

        Assert.Single(frames);
    }

    [Fact]
    public void PushSamples_LongerThanOneWindow_EmitsFramesAtHopSpacing()
    {
        using var source = new WaterfallSource(sampleRate: 8000, windowSize: 64, hopSize: 32);
        var frames = new List<WaterfallFrame>();
        source.Frames.Subscribe(frames.Add);

        // 64 (first frame) + 3*32 (three more hops) = 160 samples -> frames at 64, 96, 128, 160.
        source.PushSamples(new float[160]);

        Assert.Equal(4, frames.Count);
    }

    [Fact]
    public void PushSamples_PureToneAtKnownFrequency_PeaksAtExpectedBin()
    {
        const int sampleRate = 8000;
        const int windowSize = 64;
        const int targetBin = 8; // 8 * (8000/64) = 1000 Hz
        using var source = new WaterfallSource(sampleRate, windowSize);
        var frames = new List<WaterfallFrame>();
        source.Frames.Subscribe(frames.Add);

        var samples = new float[windowSize];
        for (var i = 0; i < windowSize; i++)
        {
            samples[i] = MathF.Sin(2f * MathF.PI * targetBin * i / windowSize);
        }

        source.PushSamples(samples);

        Assert.Single(frames);
        var magnitudes = frames[0].MagnitudesDb;
        var peakBin = 0;
        for (var k = 1; k < magnitudes.Count; k++)
        {
            if (magnitudes[k] > magnitudes[peakBin])
            {
                peakBin = k;
            }
        }

        Assert.Equal(targetBin, peakBin);
        Assert.Equal((double)sampleRate / windowSize, frames[0].BinWidthHz, precision: 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveSampleRate_Throws(int sampleRate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WaterfallSource(sampleRate));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)] // not a power of two
    [InlineData(-8)]
    public void Constructor_InvalidWindowSize_Throws(int windowSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WaterfallSource(sampleRate: 8000, windowSize: windowSize));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(128)] // larger than windowSize (64)
    public void Constructor_InvalidHopSize_Throws(int hopSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WaterfallSource(sampleRate: 8000, windowSize: 64, hopSize: hopSize));
    }

    [Fact]
    public void PushSamples_HopSizeEqualsWindowSize_NoOverlap_EmitsBackToBackFrames()
    {
        // Closes a coverage gap flagged by Tier A Batch 9 chunk 9a (docs/functional-audit-playbook.md):
        // hopSize==windowSize (no overlap, keep=0) is constructor-valid but was never exercised by
        // any test -- traced by hand to be correct (Array.Copy with a zero length is legal, no
        // throw), pinned here directly rather than left as an implicit boundary.
        const int windowSize = 64;
        using var source = new WaterfallSource(sampleRate: 8000, windowSize: windowSize, hopSize: windowSize);
        var frames = new List<WaterfallFrame>();
        source.Frames.Subscribe(frames.Add);

        source.PushSamples(new float[windowSize * 2]);

        Assert.Equal(2, frames.Count);
    }

    [Fact]
    public void PushSamples_OverlapRetention_CarriesForwardTheCorrectHalf_NotTheWrongOne()
    {
        // Closes a coverage gap flagged by Tier A Batch 9 chunk 9a: every existing multi-frame test
        // above asserts only FRAME COUNT, never frame CONTENT -- a bug retaining the WRONG half of
        // the window across a hop (e.g. Array.Copy's source offset) would leave all of them green.
        // A tone confined to the FIRST hop only, followed by silence, discriminates: the correctly
        // retained second frame must carry forward SILENCE (samples[hopSize..windowSize), which are
        // all zero here), not the tone-containing first hop.
        const int sampleRate = 8000;
        const int windowSize = 64;
        const int hopSize = 32;
        const int targetBin = 8; // 8 * (8000/64) = 1000Hz

        using var source = new WaterfallSource(sampleRate, windowSize, hopSize);
        var frames = new List<WaterfallFrame>();
        source.Frames.Subscribe(frames.Add);

        var samples = new float[96]; // one full window (64) + one more hop (32)
        for (var i = 0; i < hopSize; i++)
        {
            samples[i] = MathF.Sin(2f * MathF.PI * targetBin * i / windowSize); // tone confined to the FIRST hop only
        }

        // samples[32..95] left at 0f -- pure silence for the rest.
        source.PushSamples(samples);

        Assert.Equal(2, frames.Count);
        Assert.True(frames[0].MagnitudesDb[targetBin] > -60f, $"expected a real tone peak in frame 0, got {frames[0].MagnitudesDb[targetBin]}dB");

        // Frame 1 = retained samples[32..63] (must be silence) + new samples[64..95] (silence) -- a
        // wrong-half retain would instead carry the tone-containing first hop forward here.
        Assert.All(frames[1].MagnitudesDb, db => Assert.True(db < -150f, $"expected near-floor silence in frame 1, got {db}dB -- looks like the wrong half was retained."));
    }

    [Theory]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(float.NaN)]
    public void PushSamples_NonFiniteSample_EmittedFrameHasNoNonFiniteBins(float nonFiniteValue)
    {
        // ultracode audit finding #18: legacy clamps input to +-32768 before windowing; this port had
        // no equivalent guard, and _hannWindow[0]==0f means Inf*0=NaN corrupts the WHOLE frame
        // (windowing alone doesn't isolate the damage to just the one bad sample's own bin).
        using var source = new WaterfallSource(sampleRate: 8000, windowSize: 64);
        var frames = new List<WaterfallFrame>();
        source.Frames.Subscribe(frames.Add);

        var samples = new float[64];
        samples[10] = nonFiniteValue;
        source.PushSamples(samples);

        Assert.Single(frames);
        Assert.All(frames[0].MagnitudesDb, db => Assert.True(float.IsFinite(db), $"Non-finite bin value {db} -- a single non-finite input sample corrupted the whole frame."));
    }

    // Restart-required-settings backlog item 4 (2026-08-27): SampleRate is now live-settable via
    // IWaterfallSourceReconfiguration.RequestSampleRate -- applies immediately (no swap/idle-gating,
    // unlike the decoder's own equivalent), since the accumulator/Hann window are rate-independent.

    [Fact]
    public void RequestSampleRate_UpdatesSampleRateProperty_AndSubsequentFrameBinWidth()
    {
        const int windowSize = 64;
        using var source = new WaterfallSource(sampleRate: 8000, windowSize: windowSize);
        var reconfig = Assert.IsAssignableFrom<IWaterfallSourceReconfiguration>(source);
        var frames = new List<WaterfallFrame>();
        source.Frames.Subscribe(frames.Add);

        reconfig.RequestSampleRate(16000);

        Assert.Equal(16000, source.SampleRate);

        source.PushSamples(new float[windowSize]);

        Assert.Single(frames);
        Assert.Equal((double)16000 / windowSize, frames[0].BinWidthHz, precision: 6);
    }

    [Fact]
    public void RequestSampleRate_DiscardsPartiallyFilledAccumulator_NotMixedAcrossRates()
    {
        // Round-4 plan-review nit N2: without the reset, up to windowSize-1 old-rate samples already
        // sitting in the accumulator would get FFT'd together with new-rate samples.
        const int windowSize = 64;
        const int hopSize = 32;
        using var source = new WaterfallSource(sampleRate: 8000, windowSize: windowSize, hopSize: hopSize);
        var reconfig = Assert.IsAssignableFrom<IWaterfallSourceReconfiguration>(source);
        var frames = new List<WaterfallFrame>();
        source.Frames.Subscribe(frames.Add);

        source.PushSamples(new float[hopSize]); // partially fills the accumulator, no frame yet
        reconfig.RequestSampleRate(16000);

        source.PushSamples(new float[windowSize - 1]); // one short of a full window if the reset didn't happen

        Assert.Empty(frames); // proves the accumulator was reset to 0, not left at hopSize
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RequestSampleRate_NonPositive_Throws(int sampleRate)
    {
        using var source = new WaterfallSource(sampleRate: 8000);
        var reconfig = Assert.IsAssignableFrom<IWaterfallSourceReconfiguration>(source);

        Assert.Throws<ArgumentOutOfRangeException>(() => reconfig.RequestSampleRate(sampleRate));
    }

    [Fact]
    public void PushSamples_ThrowingFirstSubscriber_PropagatesUncaught_SkipsLaterSubscriber()
    {
        // T1-2 (production_audit.md): proves IWaterfallSource's own documented contract -- unlike
        // IRadioController.StateChanges, a Frames subscriber's exception is NOT caught/contained here;
        // it propagates straight out of PushSamples. Subject<T>.OnNext also skips notifying any
        // subscriber registered after the one that threw, same as RadioController's own
        // PollLoop_SurvivesAThrowingStateChangesSubscriber test relies on -- this is Subject<T>'s own
        // documented behavior, not something WaterfallSource adds.
        using var source = new WaterfallSource(sampleRate: 8000, windowSize: 64);
        var expected = new InvalidOperationException("Injected first subscriber failure.");
        var laterFrames = new List<WaterfallFrame>();
        source.Frames.Subscribe(_ => throw expected);
        source.Frames.Subscribe(laterFrames.Add);

        var actual = Assert.Throws<InvalidOperationException>(() => source.PushSamples(new float[64]));

        Assert.Same(expected, actual);
        Assert.Empty(laterFrames);
    }

    [Fact]
    public void PushSamples_AfterAThrowingSubscriber_DoesNotReemitAStaleDuplicateFrame()
    {
        // T1-2 (production_audit.md), real bug fixed: the accumulator's slide-forward used to run
        // AFTER _frames.OnNext, so a throwing subscriber left it un-advanced -- the VERY NEXT
        // PushSamples call re-triggered the same full-window branch with the SAME stale accumulator
        // content before consuming any of the newly-pushed samples, silently re-publishing a stale
        // duplicate frame. Confirmed empirically before the fix (a duplicate all-zeros -180dB-floor
        // frame, then the genuinely fresh one) -- this test pins the fix: exactly one frame comes out,
        // and it reflects the FRESH data, not the discarded window.
        const int windowSize = 8;
        const int hopSize = 4;
        using var source = new WaterfallSource(sampleRate: 8000, windowSize: windowSize, hopSize: hopSize);
        var shouldThrow = true;
        var frames = new List<WaterfallFrame>();
        source.Frames.Subscribe(f =>
        {
            if (shouldThrow)
            {
                shouldThrow = false;
                throw new InvalidOperationException("Injected one-time subscriber failure.");
            }

            frames.Add(f);
        });

        Assert.Throws<InvalidOperationException>(() => source.PushSamples(new float[windowSize])); // all zeros
        Assert.Empty(frames);

        source.PushSamples(Enumerable.Repeat(1f, hopSize).ToArray());

        var frame = Assert.Single(frames);
        // A stale re-emit of the all-zeros window would show every bin at the -180dB silence floor
        // (20*log10(max(0, 1e-9))); the fresh frame (half zeros, half real data after the hop-size
        // slide) must not.
        Assert.Contains(frame.MagnitudesDb, m => m > -180f);
    }
}
