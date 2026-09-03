namespace ScanlineStudio.Core.Cw.Tests;

/// <summary>Isolates <see cref="ClassicalCwDecoder.DetectKeying"/> from the rest of the pipeline --
/// hand-constructed synthetic envelope arrays, not real audio, so these pin the coarse-gate/Otsu
/// logic itself. Both tests here reproduce real bugs a full round-trip test already caught (via
/// <c>ClassicalCwDecoderTests</c>' own WPM-range theory failing at 5 WPM) -- kept as separate,
/// narrower regression tests so a future change to this method specifically gets fast, precise
/// feedback instead of having to fail a much slower full-audio round-trip test to find the same
/// bug again.</summary>
public class DetectKeyingTests
{
    private const double BlockMs = 5.0;

    [Fact]
    public void DetectKeying_HighDutyCycleSignal_StillFindsTheActiveRegion()
    {
        // Real bug this pins: a duty cycle near/above 50% (measured for real "DE W1AW" text, not a
        // hypothetical) made an earlier median-relative gate (median * 2) exceed the signal's own
        // peak, since median approaches peak/2 as duty cycle approaches 50% -- an impossible gate
        // that silently rejected every block. ~55% mark blocks here reproduces that shape directly,
        // without needing a real multi-second audio buffer.
        var envelope = new double[200];
        for (var i = 0; i < envelope.Length; i++)
        {
            envelope[i] = i % 20 < 11 ? 30.0 : 0.0; // 11/20 = 55% mark.
        }

        var intervals = ClassicalCwDecoder.DetectKeying(envelope, BlockMs);

        Assert.NotNull(intervals);
        Assert.True(intervals!.Count > 1, "Expected multiple mark/space intervals, not a single collapsed region.");
    }

    [Fact]
    public void DetectKeying_LowDutyCycleSignal_StillFindsTheActiveRegion()
    {
        // The opposite extreme -- a sparse, low-duty-cycle signal (a short ID's own mark time is a
        // small fraction of a long capture window, fsk_cwid.md §8.4 step 3's own called-out case)
        // must not regress now that the gate no longer depends on the median at all.
        var envelope = new double[200];
        for (var i = 0; i < envelope.Length; i++)
        {
            envelope[i] = i % 20 < 2 ? 30.0 : 0.0; // 2/20 = 10% mark.
        }

        var intervals = ClassicalCwDecoder.DetectKeying(envelope, BlockMs);

        Assert.NotNull(intervals);
        Assert.True(intervals!.Count > 1);
    }

    [Fact]
    public void DetectKeying_MultipleSeparateMarkBursts_SpansFromFirstToLastNotJustTheLongestRun()
    {
        // Real bug this pins: an earlier version of the active-region finder returned the SINGLE
        // longest contiguous above-gate run (i.e. one dah), not the span from the transmission's
        // first mark to its last -- a full "DE W1AW" round-trip decoded only its last one or two
        // characters instead of the whole string. Three short bursts separated by real gaps,
        // mirroring that shape directly.
        var envelope = new double[300];
        void Mark(int start, int length)
        {
            for (var i = start; i < start + length; i++)
            {
                envelope[i] = 30.0;
            }
        }

        Mark(10, 5);
        Mark(100, 5);
        Mark(250, 5); // far apart -- a "longest single run" bug would only ever see one of these.

        var intervals = ClassicalCwDecoder.DetectKeying(envelope, BlockMs);

        Assert.NotNull(intervals);
        var markCount = intervals!.Count(iv => iv.IsMark);
        Assert.Equal(3, markCount);
    }

    [Fact]
    public void DetectKeying_CompleteSilence_ReturnsNull()
    {
        var envelope = new double[200];
        Assert.Null(ClassicalCwDecoder.DetectKeying(envelope, BlockMs));
    }

    [Fact]
    public void DetectKeying_EmptyEnvelope_ReturnsNull()
    {
        Assert.Null(ClassicalCwDecoder.DetectKeying([], BlockMs));
    }
}
