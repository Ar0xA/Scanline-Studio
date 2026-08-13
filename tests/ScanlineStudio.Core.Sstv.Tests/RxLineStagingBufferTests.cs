namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// RX buffer subsystem Phase 4 -- isolated tests for <see cref="RxLineStagingBuffer"/>, before any
/// decoder wiring exists (Phase 5). No decode internals involved.
/// </summary>
public class RxLineStagingBufferTests
{
    [Theory]
    [InlineData(11025, 3_116_767)] // 257L*1100*11025/1000, computed independently (not copy-pasted from production's own expression) -- 3,116,767,500/1000 truncates
    [InlineData(22050, 6_233_535)] // 257L*1100*22050/1000 -- 6,233,535,000/1000, exact (no truncation)
    [InlineData(44100, 12_467_070)] // 257L*1100*44100/1000 -- 12,467,070,000/1000, exact (no truncation) -- the rate whose INTERMEDIATE product would overflow plain C# `int` arithmetic (not a legacy bug -- legacy's own SampFreq is `double`, see the dedicated regression test below), matching this port's own constructor's `(long)` promotion
    public void CapacitySamples_MatchesLegacyFormula(int sampleRate, int expectedCapacity)
    {
        var buffer = new RxLineStagingBuffer(sampleRate);

        Assert.Equal(expectedCapacity, buffer.CapacitySamples);
    }

    [Fact]
    public void CapacitySamples_At44100Hz_DoesNotOverflowIntermediateIntArithmetic()
    {
        // Regression test for a real C#-side hazard (see RxLineStagingBuffer's own constructor doc
        // comment -- corrected after auditor code review from an earlier, WRONG claim that this was a
        // legacy bug; legacy's own SampFreq is `double` (ComLib.cpp:47), so legacy never overflows
        // here at all). This port's own `sampleRate` parameter is `int`, and `257 * 1100 * sampleRate`
        // WOULD overflow at SampFreq=44100 (intermediate product 12,467,070,000, vs int.MaxValue's
        // ~2.147 billion) if computed in plain `int` arithmetic, even though the final result after
        // `/1000` (~12.47 million) fits easily. A naive re-introduction of plain `int` arithmetic here
        // (e.g. "simplifying" the constructor's `(long)` cast away) would silently wrap to a NEGATIVE
        // capacity at this exact sample rate -- this test pins a large POSITIVE value specifically to
        // catch that regression, not just "some value."
        var buffer = new RxLineStagingBuffer(44100);

        Assert.True(buffer.CapacitySamples > 0, $"CapacitySamples at 44100Hz was {buffer.CapacitySamples} -- expected a large positive value; a negative or small value indicates the intermediate-overflow hazard has regressed.");
        Assert.True(buffer.CapacitySamples > 10_000_000, $"CapacitySamples at 44100Hz was {buffer.CapacitySamples}, suspiciously small for ~257 lines at up to 1100ms each -- expected roughly 12.4 million.");
    }

    [Fact]
    public void Constructor_NonPositiveSampleRate_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RxLineStagingBuffer(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RxLineStagingBuffer(-11025));
    }

    [Fact]
    public void TryAppendLine_BelowCapacity_SucceedsAndValuesAreReadableInOrder()
    {
        var buffer = new RxLineStagingBuffer(11025);
        double[] demod = [1.0, 2.0, 3.0];
        double[] sync = [10.0, 20.0, 30.0];

        var accepted = buffer.TryAppendLine(demod, sync);

        Assert.True(accepted);
        Assert.Equal(3, buffer.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(demod[i], buffer.DemodulatedAt(i));
            Assert.Equal(sync[i], buffer.SyncEnvelopeAt(i));
        }
    }

    [Fact]
    public void TryAppendLine_MultipleLines_AccumulatesInChronologicalOrder()
    {
        var buffer = new RxLineStagingBuffer(11025);

        Assert.True(buffer.TryAppendLine([1.0, 2.0], [-1.0, -2.0]));
        Assert.True(buffer.TryAppendLine([3.0, 4.0], [-3.0, -4.0]));

        Assert.Equal(4, buffer.Count);
        Assert.Equal([1.0, 2.0, 3.0, 4.0], new[] { buffer.DemodulatedAt(0), buffer.DemodulatedAt(1), buffer.DemodulatedAt(2), buffer.DemodulatedAt(3) });
        Assert.Equal([-1.0, -2.0, -3.0, -4.0], new[] { buffer.SyncEnvelopeAt(0), buffer.SyncEnvelopeAt(1), buffer.SyncEnvelopeAt(2), buffer.SyncEnvelopeAt(3) });
    }

    [Fact]
    public void TryAppendLine_ExactlyAtCapacity_IsRejected_LegacyStrictLessThan()
    {
        // Round-1 code-review finding on an earlier version of this test/method: legacy's own admission
        // test (Main.cpp:4999/:5242) is `((m_wStgLine+1)*SSTVSET.m_WD) < m_RxBufAllocSize` -- STRICT
        // less-than, evaluated against the POST-append total. A line that would bring the total to
        // EXACTLY allocSize therefore fails the `<` test and is rejected, same as any line that would
        // overshoot it -- legacy always leaves at least one element of slack unused. An earlier version
        // of this buffer's own boundary condition (`Count + length > Capacity`) wrongly accepted the
        // exact-fill case; fixed to `>=`.
        const int sampleRate = 1000; // CapacitySamples = 257*1100*1000/1000 = 282,700
        var buffer = new RxLineStagingBuffer(sampleRate);
        var line = new double[buffer.CapacitySamples];

        var accepted = buffer.TryAppendLine(line, line);

        Assert.False(accepted);
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void TryAppendLine_OneLessThanCapacity_Succeeds_TheRealReachableMaximum()
    {
        // The actual maximum Count this buffer can ever reach is CapacitySamples-1 (one less than the
        // nominal capacity), matching legacy's own real "always one element of slack" behavior proven
        // by the rejection test above -- this test confirms that maximum IS reachable, not just that
        // the boundary above it is rejected.
        const int sampleRate = 1000; // CapacitySamples = 282,700
        var buffer = new RxLineStagingBuffer(sampleRate);
        var line = new double[buffer.CapacitySamples - 1];

        var accepted = buffer.TryAppendLine(line, line);

        Assert.True(accepted);
        Assert.Equal(buffer.CapacitySamples - 1, buffer.Count);
    }

    [Fact]
    public void TryAppendLine_WouldExceedCapacity_RejectsWholesale_NoPartialLineStaged()
    {
        const int sampleRate = 1000; // CapacitySamples = 282,700
        var buffer = new RxLineStagingBuffer(sampleRate);
        var almostFull = new double[buffer.CapacitySamples - 2];
        Assert.True(buffer.TryAppendLine(almostFull, almostFull));
        Assert.Equal(buffer.CapacitySamples - 2, buffer.Count);

        // A 2-sample line would land exactly ON capacity (rejected, per the strict-less-than test
        // above) -- must be rejected WHOLESALE (not truncated to fit the 1 remaining slot below the
        // real reachable maximum), matching legacy's own "no partial line ever staged" behavior. Count
        // must be completely unchanged, not partially advanced.
        var accepted = buffer.TryAppendLine([1.0, 2.0], [3.0, 4.0]);

        Assert.False(accepted);
        Assert.Equal(buffer.CapacitySamples - 2, buffer.Count);
    }

    [Fact]
    public void TryAppendLine_AfterRejection_InProgressStagedDataIsUnaffected_AndFurtherFittingAppendsStillWork()
    {
        const int sampleRate = 1000; // CapacitySamples = 282,700
        var buffer = new RxLineStagingBuffer(sampleRate);
        var almostFull = new double[buffer.CapacitySamples - 2];
        buffer.TryAppendLine(almostFull, almostFull);

        buffer.TryAppendLine([1.0, 2.0], [3.0, 4.0]); // rejected, per the test above

        // The exactly-1-sample-remaining line still fits (lands at CapacitySamples-1, the real
        // reachable maximum) and must still succeed after a prior rejection -- capture doesn't "jam"
        // once a too-big line is rejected, matching legacy's own per-call (not per-buffer-lifetime)
        // admission test.
        var accepted = buffer.TryAppendLine([99.0], [98.0]);

        Assert.True(accepted);
        Assert.Equal(buffer.CapacitySamples - 1, buffer.Count);
        Assert.Equal(99.0, buffer.DemodulatedAt(buffer.CapacitySamples - 2));
        Assert.Equal(98.0, buffer.SyncEnvelopeAt(buffer.CapacitySamples - 2));
    }

    [Fact]
    public void TryAppendLine_MismatchedStreamLengths_Throws()
    {
        var buffer = new RxLineStagingBuffer(11025);

        Assert.Throws<ArgumentException>(() => buffer.TryAppendLine([1.0, 2.0, 3.0], [1.0, 2.0]));
    }

    [Fact]
    public void LineCount_TracksSuccessfulAppendsOnly()
    {
        // RX buffer subsystem Phase 6a round-1 code-review addition: this port's own per-line staged
        // sample count is NOT a legacy-style constant (unlike legacy's own fixed m_WD) -- LineCount
        // must be tracked directly, not derivable via division against any single stride.
        var buffer = new RxLineStagingBuffer(1000); // CapacitySamples = 282,700

        Assert.Equal(0, buffer.LineCount);
        buffer.TryAppendLine([1.0, 2.0], [3.0, 4.0]);
        Assert.Equal(1, buffer.LineCount);
        buffer.TryAppendLine([5.0], [6.0]);
        Assert.Equal(2, buffer.LineCount);
    }

    [Fact]
    public void LineCount_DoesNotAdvance_OnARejectedAppend()
    {
        var buffer = new RxLineStagingBuffer(1000);
        var tooBig = new double[buffer.CapacitySamples];
        var accepted = buffer.TryAppendLine(tooBig, tooBig); // exact-fill, rejected per this class's own strict-less-than boundary

        Assert.False(accepted);
        Assert.Equal(0, buffer.LineCount);
    }

    [Fact]
    public void SampleCountThroughLine_ReflectsVaryingPerLineWidths()
    {
        // This is the whole point of tracking line boundaries explicitly instead of deriving a line
        // count via division against a single stride -- these three lines are NOT the same width,
        // exactly the real-world case (fractional-carry rounding, mid-reception Auto-Slant commits)
        // that made a stride-based derivation wrong for this port specifically.
        var buffer = new RxLineStagingBuffer(1000);
        buffer.TryAppendLine([1.0, 2.0, 3.0], [0.0, 0.0, 0.0]); // 3 samples
        buffer.TryAppendLine([4.0, 5.0], [0.0, 0.0]); // 2 samples
        buffer.TryAppendLine([6.0, 7.0, 8.0, 9.0], [0.0, 0.0, 0.0, 0.0]); // 4 samples

        Assert.Equal(3, buffer.LineCount);
        Assert.Equal(0, buffer.SampleCountThroughLine(0));
        Assert.Equal(3, buffer.SampleCountThroughLine(1));
        Assert.Equal(5, buffer.SampleCountThroughLine(2));
        Assert.Equal(9, buffer.SampleCountThroughLine(3));
        Assert.Equal(9, buffer.SampleCountThroughLine(100)); // clamped to LineCount, matches full Count
        Assert.Equal(buffer.Count, buffer.SampleCountThroughLine(buffer.LineCount));
    }

    [Fact]
    public void LineCount_And_SampleCountThroughLine_ResetByClear()
    {
        var buffer = new RxLineStagingBuffer(1000);
        buffer.TryAppendLine([1.0, 2.0], [3.0, 4.0]);

        buffer.Clear();

        Assert.Equal(0, buffer.LineCount);
        Assert.Equal(0, buffer.SampleCountThroughLine(5));
    }

    [Fact]
    public void Clear_ResetsCountToZero_ButNotCapacity()
    {
        var buffer = new RxLineStagingBuffer(11025);
        buffer.TryAppendLine([1.0, 2.0], [3.0, 4.0]);
        var capacityBeforeClear = buffer.CapacitySamples;

        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.Equal(capacityBeforeClear, buffer.CapacitySamples);
    }

    [Fact]
    public void Clear_ThenAppend_StartsFreshAtIndexZero_NotAppendingToStaleData()
    {
        var buffer = new RxLineStagingBuffer(11025);
        buffer.TryAppendLine([1.0, 2.0], [3.0, 4.0]);

        buffer.Clear();
        buffer.TryAppendLine([5.0], [6.0]);

        Assert.Equal(1, buffer.Count);
        Assert.Equal(5.0, buffer.DemodulatedAt(0));
        Assert.Equal(6.0, buffer.SyncEnvelopeAt(0));
    }

    [Fact]
    public void DemodulatedAt_OutOfRange_Throws()
    {
        var buffer = new RxLineStagingBuffer(11025);
        buffer.TryAppendLine([1.0], [2.0]);

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.DemodulatedAt(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.DemodulatedAt(-1));
    }
}
