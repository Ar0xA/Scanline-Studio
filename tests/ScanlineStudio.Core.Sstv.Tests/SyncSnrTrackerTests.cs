namespace ScanlineStudio.Core.Sstv.Tests;

public class SyncSnrTrackerTests
{
    private const int Rate = 11025;
    private const int PulseLength = 53; // 4.8 ms
    private const int Positions = 45;
    private const int Trim = 8;

    /// <summary>Span whose sync pulse (1200 Hz) starts at <paramref name="pulseStart"/>, 1500 Hz around it.</summary>
    private static float[] Span(int pulseStart, double snrDb, Random random)
    {
        var length = Positions - 1 + PulseLength;
        var sigma = double.IsPositiveInfinity(snrDb) ? 0 : Math.Sqrt(0.5 / Math.Pow(10, snrDb / 10) * (Rate / 2.0) / SyncSnrEstimator.BandWidthHz);
        var samples = new float[length];
        var phase = random.NextDouble() * 6.28;
        for (var i = 0; i < length; i++)
        {
            phase += 2 * Math.PI * (i >= pulseStart && i < pulseStart + PulseLength ? 1200 : 1500) / Rate;
            samples[i] = (float)(Math.Sin(phase) + (sigma * SyncSnrEstimatorTests.Gaussian(random)));
        }

        return samples;
    }

    [Fact]
    public void FirstEightLines_OnlyAcquire_ThenEveryLineContributes()
    {
        var tracker = new SyncSnrTracker();
        var random = new Random(1);
        for (var line = 0; line < 12; line++)
        {
            var outcome = tracker.ProcessLine(line, Span(20, 20, random), Positions, PulseLength, Trim, Rate, 1200);
            if (line < SyncSnrTracker.PlacementLines)
            {
                Assert.Equal(-1, outcome.PlacedStart);
            }
            else
            {
                Assert.InRange(outcome.PlacedStart, 18, 22);
            }

            Assert.InRange(outcome.LocatedStart, 16, 24);
        }

        Assert.Equal(12, tracker.LinesSeen);
        Assert.Equal(4, tracker.LinesContributed);
        Assert.Equal(4, tracker.StoredLineCount);
        Assert.InRange(tracker.ReceptionSnrDb, 16, 24);
        Assert.InRange(tracker.LiveSnrDb, 16, 24);
    }

    [Fact]
    public void Window_SitsAtTheMedianOfThePreviousLines_NotTheLinesOwnBest()
    {
        var tracker = new SyncSnrTracker();
        var random = new Random(2);
        foreach (var start in new[] { 17, 15, 19, 16, 18, 17, 16, 18 })
        {
            tracker.ProcessLine(0, Span(start, double.PositiveInfinity, random), Positions, PulseLength, Trim, Rate, 1200);
        }

        // Previous best starts scatter around 17 with no drift, regardless of this line's own pulse at 30.
        var outcome = tracker.ProcessLine(8, Span(30, double.PositiveInfinity, random), Positions, PulseLength, Trim, Rate, 1200);

        Assert.InRange(outcome.PlacedStart, 16, 18);
        Assert.InRange(outcome.LocatedStart, 29, 31);
    }

    [Fact]
    public void SteadyDrift_IsExtrapolated_SoTheWindowKeepsUpWithThePulse()
    {
        var tracker = new SyncSnrTracker();
        var random = new Random(12);
        foreach (var start in new[] { 10, 12, 14, 16, 18, 20, 22, 24 })
        {
            tracker.ProcessLine(0, Span(start, double.PositiveInfinity, random), Positions, PulseLength, Trim, Rate, 1200);
        }

        // 2 samples per line: the next pulse is at 26, not at the history median of 17.
        var outcome = tracker.ProcessLine(8, Span(26, double.PositiveInfinity, random), Positions, PulseLength, Trim, Rate, 1200);

        Assert.InRange(outcome.PlacedStart, 25, 27);
    }

    [Fact]
    public void SearchBias_FollowsOnlyAFullHistory_AndEightEdgeLinesFallBackToTheFormulaCentre()
    {
        var tracker = new SyncSnrTracker();
        var random = new Random(13);
        tracker.ProcessLine(0, Span(5, double.PositiveInfinity, random), Positions, PulseLength, Trim, Rate, 1200, spanOffset: -22);
        Assert.Equal(0, tracker.SearchBias);

        for (var line = 1; line < 8; line++)
        {
            tracker.ProcessLine(line, Span(5, double.PositiveInfinity, random), Positions, PulseLength, Trim, Rate, 1200, spanOffset: -22);
        }

        // Every best start was span index 5 = offset −17 from the formula centre.
        Assert.InRange(tracker.SearchBias, -18, -16);

        // Trim so large no SNR window fits: these lines only locate, so nothing else moves.
        for (var line = 8; line < 16; line++)
        {
            tracker.ProcessLine(line, Span(Positions + 10, double.PositiveInfinity, random), Positions, PulseLength, 26, Rate, 1200, spanOffset: -22);
        }

        Assert.Equal(0, tracker.SearchBias);
    }

    [Fact]
    public void ResetPlacement_RestartsAcquisition_ButKeepsTheStore()
    {
        var tracker = new SyncSnrTracker();
        var random = new Random(3);
        for (var line = 0; line < 10; line++)
        {
            tracker.ProcessLine(line, Span(20, 20, random), Positions, PulseLength, Trim, Rate, 1200);
        }

        tracker.ResetPlacement();
        var outcome = tracker.ProcessLine(10, Span(20, 20, random), Positions, PulseLength, Trim, Rate, 1200);

        Assert.Equal(-1, outcome.PlacedStart);
        Assert.Equal(2, tracker.StoredLineCount);
    }

    [Fact]
    public void SameLineMeasuredTwice_Overwrites_NeverDoubleCounts()
    {
        var tracker = new SyncSnrTracker();
        var random = new Random(4);
        for (var line = 0; line < 9; line++)
        {
            tracker.ProcessLine(line, Span(20, 20, random), Positions, PulseLength, Trim, Rate, 1200);
        }

        Assert.Equal(1, tracker.StoredLineCount);

        // A replay snap re-decodes line 8 with a much worse reading: the store keeps one entry for it.
        tracker.ProcessLine(8, Span(20, 0, random), Positions, PulseLength, Trim, Rate, 1200);

        Assert.Equal(1, tracker.StoredLineCount);
        Assert.InRange(tracker.ReceptionSnrDb, -4, 4);
    }

    [Fact]
    public void ResetReception_ClearsEverything()
    {
        var tracker = new SyncSnrTracker();
        var random = new Random(5);
        for (var line = 0; line < 10; line++)
        {
            tracker.ProcessLine(line, Span(20, 20, random), Positions, PulseLength, Trim, Rate, 1200);
        }

        tracker.ResetReception();

        Assert.Equal(0, tracker.StoredLineCount);
        Assert.Equal(0, tracker.LinesSeen);
        Assert.True(double.IsNaN(tracker.LiveSnrDb));
        Assert.True(double.IsNaN(tracker.ReceptionSnrDb));
        Assert.Equal(-1, tracker.ProcessLine(10, Span(20, 20, random), Positions, PulseLength, Trim, Rate, 1200).PlacedStart);
    }

    [Fact]
    public void EndLive_KeepsThePerPictureFigure()
    {
        var tracker = new SyncSnrTracker();
        var random = new Random(6);
        for (var line = 0; line < 10; line++)
        {
            tracker.ProcessLine(line, Span(20, 20, random), Positions, PulseLength, Trim, Rate, 1200);
        }

        tracker.EndLive();

        Assert.True(double.IsNaN(tracker.LiveSnrDb));
        Assert.False(double.IsNaN(tracker.ReceptionSnrDb));
    }

    [Fact]
    public void EdgeLines_AreCounted_AndDoNotFeedPlacement()
    {
        var tracker = new SyncSnrTracker();
        var random = new Random(7);
        for (var line = 0; line < 9; line++)
        {
            tracker.ProcessLine(line, Span(Positions + 10, double.PositiveInfinity, random), Positions, PulseLength, Trim, Rate, 1200);
        }

        Assert.Equal(9, tracker.EdgeLines);
        Assert.Equal(0, tracker.LinesContributed);
    }

    [Fact]
    public void Median_MatchesASortedReference_OddEvenAndDuplicates()
    {
        var random = new Random(21);
        for (var trial = 0; trial < 500; trial++)
        {
            var values = Enumerable.Range(0, 1 + random.Next(60)).Select(_ => (double)random.Next(12)).ToList();
            var sorted = values.OrderBy(v => v).ToList();
            var mid = sorted.Count / 2;
            var expected = sorted.Count % 2 == 1 ? sorted[mid] : 0.5 * (sorted[mid - 1] + sorted[mid]);

            Assert.Equal(expected, SyncSnrTracker.Median(values));
        }
    }

    [Fact]
    public void RepeatedEdgeFallbacks_WidenTheRange_UpToTheCap()
    {
        var tracker = new SyncSnrTracker();
        var random = new Random(14);
        Assert.Equal(1, tracker.RangeMultiplier);
        for (var line = 0; line < 8 * 3; line++)
        {
            tracker.ProcessLine(line, Span(Positions + 10, double.PositiveInfinity, random), Positions, PulseLength, 26, Rate, 1200);
        }

        Assert.Equal(SyncSnrTracker.MaxRangeMultiplier, tracker.RangeMultiplier);
        tracker.ResetReception();
        Assert.Equal(1, tracker.RangeMultiplier);
    }

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(-0.5, 1.0)]
    [InlineData(1.0, 0.0)]
    public void ToDb_NoToneOrNoNoise_IsNoFigure(double tone, double noise)
    {
        Assert.True(double.IsNaN(SyncSnrTracker.ToDb(tone, noise)));
    }

    [Fact]
    public void ToDb_TinyPositiveTone_IsFlooredNotNaN()
    {
        Assert.Equal(SyncSnrTracker.FloorDb, SyncSnrTracker.ToDb(1e-12, 1.0));
    }

    [Fact]
    public void LiveFigure_CoversOnlyTheLast16Lines()
    {
        var tracker = new SyncSnrTracker();
        var random = new Random(8);
        for (var line = 0; line < 30; line++)
        {
            tracker.ProcessLine(line, Span(20, 30, random), Positions, PulseLength, Trim, Rate, 1200);
        }

        for (var line = 30; line < 46; line++)
        {
            tracker.ProcessLine(line, Span(20, 5, random), Positions, PulseLength, Trim, Rate, 1200);
        }

        Assert.InRange(tracker.LiveSnrDb, 2, 8);
        Assert.True(tracker.ReceptionSnrDb > tracker.LiveSnrDb + 3);
    }
}
