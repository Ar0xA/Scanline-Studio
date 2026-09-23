namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// Per-reception state for the sync-pulse SNR measurement: measured window placement, the frequency
/// search centre, the per-line store and the live/per-picture aggregates. Decode thread only (owned
/// by one <see cref="AnalogFmSstvDecoder"/>); it publishes nothing itself — the decoder copies
/// <see cref="LiveSnrDb"/>/<see cref="ReceptionSnrDb"/> into its own atomics after every line.
/// <para>Placement: every line's own best sync start (<see cref="SyncPulseLocator"/>) only feeds LATER
/// lines; a line is measured at the median of the previous <see cref="PlacementLines"/> lines' best
/// starts. So no line is measured where it scored best (no self-selection bias), slow drift is followed,
/// and a wrong lock heals. After <see cref="ResetPlacement"/> the first lines only acquire.</para>
/// </summary>
internal sealed class SyncSnrTracker
{
    internal const int PlacementLines = 8;

    /// <summary>Lines measured on the wide frequency grid before the reception centre is fixed.</summary>
    internal const int WideStageLines = 8;
    internal const double WideHalfSpanHz = 150.0;
    internal const double NarrowHalfSpanHz = 20.0;
    internal const double LocateStepHz = 10.0;
    internal const int LiveLines = 16;

    // Keyed by transmission line index: a replay snap that re-decodes a line overwrites, never double-counts.
    private readonly Dictionary<int, (double Tone, double Noise)> _lines = [];
    private readonly Queue<int> _placementHistory = new();
    private readonly List<double> _wideFits = [];
    private readonly List<double> _scratchTones = [];
    private readonly List<double> _scratchNoises = [];
    private double? _centreHz;
    private int _consecutiveEdgeLines;

    /// <summary>Largest factor the search range is widened to after repeated edge hits.</summary>
    internal const int MaxRangeMultiplier = 4;

    public double LiveSnrDb { get; private set; } = double.NaN;

    public double ReceptionSnrDb { get; private set; } = double.NaN;

    /// <summary>Lines offered to <see cref="ProcessLine"/> this reception.</summary>
    internal int LinesSeen { get; private set; }

    /// <summary>Lines whose SNR entered the store this reception.</summary>
    internal int LinesContributed { get; private set; }

    /// <summary>Lines whose own best start sat on the search-range edge (not used for placement).</summary>
    internal int EdgeLines { get; private set; }

    internal int StoredLineCount => _lines.Count;

    internal double? CentreHzForTests => _centreHz;

    public void ResetReception()
    {
        _lines.Clear();
        _wideFits.Clear();
        _centreHz = null;
        ResetPlacement();
        LinesSeen = 0;
        LinesContributed = 0;
        EdgeLines = 0;
        RangeMultiplier = 1;
        LiveSnrDb = double.NaN;
        ReceptionSnrDb = double.NaN;
    }

    /// <summary>The anchor moved or the stride changed: earlier best starts no longer apply.</summary>
    public void ResetPlacement()
    {
        _placementHistory.Clear();
        _consecutiveEdgeLines = 0;
    }

    /// <summary>Factor the decoder applies to its search half-range. Starts at 1 per reception and doubles
    /// (up to <see cref="MaxRangeMultiplier"/>) each time <see cref="PlacementLines"/> consecutive lines find
    /// their best start on the range edge: mistuning can move the sync-envelope anchor several ms away from
    /// the pulse, and a fixed range would then never find it.</summary>
    public int RangeMultiplier { get; private set; } = 1;

    /// <summary>Where the next line's search should be centred, relative to the formula centre: the median
    /// of the previous lines' best starts (0 after a reset). Following it lets the search track a sync
    /// pulse that drifts against the anchor (clock error with Auto Slant off) instead of losing it at the
    /// edge of a fixed range. Only once the history is full, so one bad early line (e.g. line 0, whose
    /// sync runs straight on from the VIS stop bit at the same tone) cannot steer the search away.</summary>
    public int SearchBias => _placementHistory.Count >= PlacementLines ? MedianStart() : 0;

    /// <summary>Live figure ends with the picture; the per-picture figure stays until the next reset.</summary>
    public void EndLive() => LiveSnrDb = double.NaN;

    /// <summary>Outcome of one line: where the SNR window started (index into the span, −1 when the line
    /// only acquired or was skipped) and the line's own located start (−1 when none).</summary>
    internal readonly record struct LineOutcome(int PlacedStart, int LocatedStart, SyncSnrEstimator.Estimate Estimate);

    /// <param name="transmissionLine">Store key.</param>
    /// <param name="span">Raw samples; candidate start p covers span[p .. p + pulseLength).</param>
    /// <param name="spanOffset">Position of span[0] relative to the formula centre, in samples; history
    /// offsets are kept relative to that centre so they stay valid while the span follows <see cref="SearchBias"/>.</param>
    /// <param name="positions">Number of candidate starts.</param>
    /// <param name="pulseLength">Sync pulse length in samples.</param>
    /// <param name="trimSamples">Samples trimmed from each end of the pulse for the SNR window.</param>
    /// <param name="sampleRate">Raw stream rate.</param>
    /// <param name="expectedToneHz">Nominal sync tone minus the AFC correction.</param>
    public LineOutcome ProcessLine(int transmissionLine, ReadOnlySpan<float> span, int positions, int pulseLength, int trimSamples, int sampleRate, double expectedToneHz, int spanOffset = 0)
    {
        LinesSeen++;
        var placed = -1;
        var estimate = SyncSnrEstimator.Estimate.Invalid;
        var windowLength = pulseLength - (2 * trimSamples);
        if (_placementHistory.Count >= PlacementLines && windowLength >= 8)
        {
            placed = Math.Clamp(MedianStart() - spanOffset, 0, positions - 1);
            estimate = Measure(span.Slice(placed + trimSamples, windowLength), sampleRate, expectedToneHz);
            if (estimate.IsValid)
            {
                _lines[transmissionLine] = (estimate.TonePower, estimate.NoisePower);
                LinesContributed++;
                Recompute(transmissionLine);
            }
        }

        var location = Locate(span, positions, pulseLength, sampleRate, expectedToneHz);
        if (location.IsValid && location.AtEdge)
        {
            EdgeLines++;
            if (++_consecutiveEdgeLines >= PlacementLines)
            {
                // The pulse has left the followed range: fall back to the formula centre and search wider.
                ResetPlacement();
                RangeMultiplier = Math.Min(RangeMultiplier * 2, MaxRangeMultiplier);
            }
        }
        else if (location.IsValid)
        {
            _consecutiveEdgeLines = 0;
            _placementHistory.Enqueue(location.Start + spanOffset);
            if (_placementHistory.Count > PlacementLines)
            {
                _placementHistory.Dequeue();
            }
        }

        return new LineOutcome(placed, location.IsValid ? location.Start : -1, estimate);
    }

    private SyncPulseLocator.Location Locate(ReadOnlySpan<float> span, int positions, int pulseLength, int sampleRate, double expectedToneHz)
    {
        if (_centreHz is { } centre)
        {
            return SyncPulseLocator.Locate(span, positions, pulseLength, sampleRate, [centre]);
        }

        var steps = (int)(WideHalfSpanHz / LocateStepHz);
        Span<double> frequencies = stackalloc double[(2 * steps) + 1];
        for (var k = 0; k < frequencies.Length; k++)
        {
            frequencies[k] = expectedToneHz + ((k - steps) * LocateStepHz);
        }

        return SyncPulseLocator.Locate(span, positions, pulseLength, sampleRate, frequencies);
    }

    private SyncSnrEstimator.Estimate Measure(ReadOnlySpan<float> window, int sampleRate, double expectedToneHz)
    {
        if (_centreHz is not { } centre)
        {
            var wide = SyncSnrEstimator.Measure(window, sampleRate, expectedToneHz, WideHalfSpanHz);
            if (wide.IsValid)
            {
                _wideFits.Add(wide.FittedHz);
                if (_wideFits.Count >= WideStageLines)
                {
                    _centreHz = Median(_wideFits);
                }
            }

            return wide;
        }

        var estimate = SyncSnrEstimator.Measure(window, sampleRate, centre, NarrowHalfSpanHz);
        if (estimate.AtGridEdge)
        {
            // Re-centre on the edge and search once more around it.
            _centreHz = estimate.FittedHz;
            estimate = SyncSnrEstimator.Measure(window, sampleRate, estimate.FittedHz, NarrowHalfSpanHz);
        }

        return estimate;
    }

    /// <summary>Predicted start for the next line: the median of the history, moved forward by its robust
    /// drift (newer-half median minus older-half median, per line) over the median's lag. Without the drift
    /// term a steady clock error (Auto Slant off) leaves the median ~4.5 lines behind the pulse.</summary>
    private int MedianStart()
    {
        var starts = _placementHistory.ToArray();
        var median = MedianOf(starts);
        if (starts.Length < PlacementLines)
        {
            return (int)Math.Round(median);
        }

        // Only a clear drift (the two halves do not overlap) is extrapolated; line-to-line scatter at low
        // SNR would otherwise be amplified into the prediction.
        var half = starts.Length / 2;
        var older = starts[..half];
        var newer = starts[half..];
        if (newer.Min() <= older.Max() && older.Min() <= newer.Max())
        {
            return (int)Math.Round(median);
        }

        // Below a sample per line the median's lag is under half a sample per line of history: not worth the noise.
        var drift = (MedianOf(newer) - MedianOf(older)) / half;
        return Math.Abs(drift) < 1.0
            ? (int)Math.Round(median)
            : (int)Math.Round(median + (drift * ((starts.Length + 1) / 2.0)));
    }

    private static double MedianOf(int[] values)
    {
        var sorted = (int[])values.Clone();
        Array.Sort(sorted);
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : 0.5 * (sorted[mid - 1] + sorted[mid]);
    }

    private void Recompute(int latestLine)
    {
        _scratchTones.Clear();
        _scratchNoises.Clear();
        foreach (var (_, value) in _lines)
        {
            _scratchTones.Add(value.Tone);
            _scratchNoises.Add(value.Noise);
        }

        ReceptionSnrDb = ToDb(Median(_scratchTones), Median(_scratchNoises));

        _scratchTones.Clear();
        _scratchNoises.Clear();
        for (var line = latestLine - LiveLines + 1; line <= latestLine; line++)
        {
            if (_lines.TryGetValue(line, out var value))
            {
                _scratchTones.Add(value.Tone);
                _scratchNoises.Add(value.Noise);
            }
        }

        LiveSnrDb = _scratchTones.Count == 0 ? double.NaN : ToDb(Median(_scratchTones), Median(_scratchNoises));
    }

    /// <summary>Floor for a noise-dominated reading, far below any display floor but still finite so it
    /// persists.</summary>
    internal const double FloorDb = -60.0;

    /// <summary>10·log10(tone/noise), floored at <see cref="FloorDb"/>. A non-positive median tone (after the
    /// captured-noise subtraction) means the windows held no tone at all — the sync pulse was not where it
    /// was measured — so it reads as no figure (NaN), not as a very low one.</summary>
    internal static double ToDb(double tone, double noise) =>
        noise > 0 && tone > 0 ? Math.Max(FloorDb, 10.0 * Math.Log10(tone / noise)) : double.NaN;

    /// <summary>Median (mean of the two middle values for an even count), by quickselect in place: the
    /// per-picture figure is recomputed every line, so a full sort per line would grow with picture height.
    /// Reorders <paramref name="values"/>.</summary>
    internal static double Median(List<double> values)
    {
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(values);
        var mid = span.Length / 2;
        var upper = Select(span, mid);
        if (span.Length % 2 == 1)
        {
            return upper;
        }

        // After Select, every element left of mid is <= span[mid]; the lower middle is their maximum.
        var lower = double.NegativeInfinity;
        for (var i = 0; i < mid; i++)
        {
            lower = Math.Max(lower, span[i]);
        }

        return 0.5 * (lower + upper);
    }

    private static double Select(Span<double> span, int k)
    {
        var left = 0;
        var right = span.Length - 1;
        while (left < right)
        {
            var pivot = span[left + ((right - left) / 2)];
            var i = left;
            var j = right;
            while (i <= j)
            {
                while (span[i] < pivot)
                {
                    i++;
                }

                while (span[j] > pivot)
                {
                    j--;
                }

                if (i <= j)
                {
                    (span[i], span[j]) = (span[j], span[i]);
                    i++;
                    j--;
                }
            }

            if (k <= j)
            {
                right = j;
            }
            else if (k >= i)
            {
                left = i;
            }
            else
            {
                break;
            }
        }

        return span[k];
    }
}
