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
    private readonly SortedDictionary<int, (double Tone, double Noise)> _lines = [];
    private readonly Queue<int> _placementHistory = new();
    private readonly List<double> _wideFits = [];
    private readonly List<double> _scratchTones = [];
    private readonly List<double> _scratchNoises = [];
    private double? _centreHz;

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
        LiveSnrDb = double.NaN;
        ReceptionSnrDb = double.NaN;
    }

    /// <summary>The anchor moved or the stride changed: earlier best starts no longer apply.</summary>
    public void ResetPlacement() => _placementHistory.Clear();

    /// <summary>Live figure ends with the picture; the per-picture figure stays until the next reset.</summary>
    public void EndLive() => LiveSnrDb = double.NaN;

    /// <summary>Outcome of one line: where the SNR window started (index into the span, −1 when the line
    /// only acquired or was skipped) and the line's own located start (−1 when none).</summary>
    internal readonly record struct LineOutcome(int PlacedStart, int LocatedStart, SyncSnrEstimator.Estimate Estimate);

    /// <param name="transmissionLine">Store key.</param>
    /// <param name="span">Raw samples; candidate start p covers span[p .. p + pulseLength).</param>
    /// <param name="positions">Number of candidate starts.</param>
    /// <param name="pulseLength">Sync pulse length in samples.</param>
    /// <param name="trimSamples">Samples trimmed from each end of the pulse for the SNR window.</param>
    /// <param name="sampleRate">Raw stream rate.</param>
    /// <param name="expectedToneHz">Nominal sync tone minus the AFC correction.</param>
    public LineOutcome ProcessLine(int transmissionLine, ReadOnlySpan<float> span, int positions, int pulseLength, int trimSamples, int sampleRate, double expectedToneHz)
    {
        LinesSeen++;
        var placed = -1;
        var estimate = SyncSnrEstimator.Estimate.Invalid;
        var windowLength = pulseLength - (2 * trimSamples);
        if (_placementHistory.Count >= PlacementLines && windowLength >= 8)
        {
            placed = MedianStart();
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
        }
        else if (location.IsValid)
        {
            _placementHistory.Enqueue(location.Start);
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

    private int MedianStart()
    {
        var starts = _placementHistory.ToArray();
        Array.Sort(starts);
        var mid = starts.Length / 2;
        return starts.Length % 2 == 1 ? starts[mid] : (starts[mid - 1] + starts[mid]) / 2;
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
        foreach (var (line, value) in _lines)
        {
            if (line > latestLine - LiveLines && line <= latestLine)
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

    /// <summary>10·log10(tone/noise); a non-positive median tone (after the captured-noise
    /// subtraction) reads as <see cref="FloorDb"/>.</summary>
    internal static double ToDb(double tone, double noise) =>
        noise > 0 ? (tone > 0 ? Math.Max(FloorDb, 10.0 * Math.Log10(tone / noise)) : FloorDb) : double.NaN;

    internal static double Median(List<double> values)
    {
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : 0.5 * (values[mid - 1] + values[mid]);
    }
}
