namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// Direct port of legacy's "Auto Slant" (`Main.cpp`'s <c>AutoStopJob</c>, the <c>KRSA-&gt;Checked</c>
/// branch only, `Main.cpp:3968-4018`, plus the shared position-history bookkeeping every call does,
/// `3964-3966`, and <c>GetSqerrPos</c>, `Main.cpp:3867-3880`) — automatic sample-clock-rate drift
/// correction, on by default in the shipped legacy config (<c>Mmsstv.ini</c>'s <c>AutoSlant=1</c>).
/// Each line, the caller reports where the sync-tone envelope peaked relative to that mode's
/// expected sync position (see <see cref="SyncEnvelopeDetector"/>); this class fits a trend line
/// through the last 5 readings, and once it has enough lines of consistent trend, computes a
/// corrected samples-per-line value and reports it once a staged confidence-threshold ladder
/// decides the drift is real (not noise).
///
/// Deliberately NOT ported (out of scope for "slant" specifically, per user direction): legacy
/// bundles this together with two *different* features in the same function --
/// "Auto Stop" (<c>sys.m_AutoStop</c>, stops the recording when the sync position looks stable,
/// i.e. the image looks finished) and "Auto Sync" (<c>sys.m_AutoSync</c>, snaps the read pointer to
/// recover from a sync glitch/jump) -- both left out here as separate concerns, not slant
/// correction. Also not ported: the <c>!SBTX-&gt;Down</c>/echo/TX-sample-offset gate and the
/// <c>m_AutoSyncCount</c> re-entrancy guard, both trivially satisfied for a pure RX decoder with no
/// simultaneous-TX concept (this port's decoder never has <c>SBTX-&gt;Down</c> true), so they're
/// correctly omitted rather than silently dropped.
///
/// Scope note on what "applying" the correction means here: legacy retroactively re-decodes the
/// *entire* image received so far at the corrected rate once a correction commits
/// (<c>RedrawSampFreq</c> replaying a staging buffer). This port instead applies a committed
/// correction going forward only (see <c>AnalogFmSstvDecoder</c>) -- for a genuinely drifting clock
/// the correction stays valid for the remainder of the transmission either way, so this is a real,
/// bounded scoping choice (the handful of already-decoded lines before the first lock aren't
/// retroactively fixed), not a silently-accepted approximation of the correction math itself, which
/// is ported exactly.
/// </summary>
internal sealed class SlantTracker
{
    private const int HistorySize = 16;
    private const int FitPoints = 5; // GetSqerrPos is only ever called with n=5 in legacy

    private readonly double[] _history = new double[HistorySize];
    private readonly double[] _limitsHz;
    private readonly int[] _thresholdLinePositions;
    private readonly double _lineDurationMs;
    private readonly double _sampleRate;
    private readonly int _mult;
    private readonly MovingAverage _correctionAverage = new(HistorySize);

    // Main.cpp:3994's `SSTVSET.m_SampFreq`/`SSTVSET.m_TW` are BOTH the evolving, already-corrected
    // values, kept in sync by SetSampFreq() recomputing m_TW = GetTiming(mode)*m_SampFreq/1000 after
    // every commit -- not the fixed hardware rate. _sampleRate above stays fixed (it matches the
    // *different*, genuinely-fixed `SampFreq` used for the m_ASLmt/clamp constants, Main.cpp:1645
    // and 4012-4014). These two mutable fields track the evolving pair; keeping them in sync is
    // exactly the bug an earlier version of this class had -- _nominalSamplesPerLine was frozen at
    // its construction-time value forever, so after the first correction the drift formula's
    // numerator (fixed _sampleRate) and denominator (stale _nominalSamplesPerLine) silently drifted
    // out of the proportional relationship legacy maintains, corrupting every correction after the
    // first. Caught by an end-to-end mistuned-rate test, not by the isolated unit tests above (which
    // never exercised more than one correction in sequence).
    private double _currentSampleRate;
    private double _nominalSamplesPerLine;

    private int _totalLinesObserved;
    private int _linesSinceBaseline;
    private int _bitMask;
    private double _baselinePosition = double.MaxValue; // 0x7fffffff sentinel: "no baseline yet"
    private bool _hasBaseline;

    /// <param name="sampleRate">This decoder's sample rate.</param>
    /// <param name="nominalSamplesPerLine">The mode's nominal (uncorrected) samples-per-line, i.e.
    /// <c>mode.LineDurationMs/1000*sampleRate</c> -- legacy's <c>SSTVSET.m_TW</c>.</param>
    /// <param name="thresholdLinePositions">Mode-dependent <c>m_ASPos[0..3]</c> (`Main.cpp:3812-3856`)
    /// -- line counts at which progressively smaller drift thresholds become active. See
    /// <see cref="SstvModeRegistry"/>'s per-mode grouping.</param>
    public SlantTracker(double sampleRate, double nominalSamplesPerLine, int[] thresholdLinePositions)
    {
        _sampleRate = sampleRate;
        _currentSampleRate = sampleRate;
        _nominalSamplesPerLine = nominalSamplesPerLine;
        _lineDurationMs = nominalSamplesPerLine / sampleRate * 1000.0;
        _thresholdLinePositions = thresholdLinePositions;
        _mult = Math.Max(1, (int)(nominalSamplesPerLine / 320.0)); // Main.cpp:3860, literal 320 regardless of actual image width

        // m_ASLmt[0..6] = {25,10,2,0.5,0.2,0.2,0.08} * SampFreq/11025 (Main.cpp:1645-1651).
        double[] literalHz = [25.0, 10.0, 2.0, 0.5, 0.2, 0.2, 0.08];
        _limitsHz = Array.ConvertAll(literalHz, v => v * sampleRate / 11025.0);
    }

    /// <summary>Reports one line's sync-envelope peak position, in samples, relative to where this
    /// mode's sync segment is expected to start (already wrapped to the representation closest to
    /// zero — see <c>AnalogFmSstvDecoder</c>). Returns a corrected samples-per-line value once a
    /// drift correction commits, else null.</summary>
    public double? ProcessLine(double relativePositionSamples)
    {
        // Main.cpp:3964-3966: unconditional history shift + push, every call.
        Array.Copy(_history, 1, _history, 0, HistorySize - 1);
        _history[HistorySize - 1] = relativePositionSamples;
        _totalLinesObserved++;

        double? result = null;

        if (_totalLinesObserved >= FitPoints)
        {
            // Main.cpp:3971-3981: jitter gate over the last 5 consecutive deltas.
            var maxDelta = 0.0;
            for (var i = HistorySize - 1; i > HistorySize - FitPoints; i--)
            {
                maxDelta = Math.Max(maxDelta, Math.Abs(_history[i] - _history[i - 1]));
            }

            if (maxDelta < 8 * _mult)
            {
                var fittedPosition = GetSqerrPos();

                if (!_hasBaseline)
                {
                    _baselinePosition = fittedPosition;
                    _linesSinceBaseline = 0;
                    _hasBaseline = true;
                }
                else if (_linesSinceBaseline >= 3)
                {
                    result = TryComputeCorrection(fittedPosition);
                }
            }
        }

        _linesSinceBaseline++;
        return result;
    }

    /// <summary><c>GetSqerrPos(5)</c> (`Main.cpp:3867-3880`) — least-squares linear fit's value at
    /// i=0 (the most recent sample) over the last 5 history entries.</summary>
    private double GetSqerrPos()
    {
        double t = 0, l = 0, tt = 0, tl = 0;
        for (var i = 0; i < FitPoints; i++)
        {
            t += i;
            var value = _history[HistorySize - 1 - i];
            l += value;
            tt += (double)i * i;
            tl += i * value;
        }

        return (l * tt - t * tl) / (FitPoints * tt - t * t);
    }

    /// <summary>Main.cpp:3994-4017 -- the drift calculation and staged threshold ladder.</summary>
    private double? TryComputeCorrection(double fittedPosition)
    {
        var d = (_baselinePosition - fittedPosition) * _currentSampleRate / _nominalSamplesPerLine / _linesSinceBaseline;
        var candidateSampleRate = _correctionAverage.Add(_currentSampleRate - d);
        d = _currentSampleRate - candidateSampleRate;

        var triggered =
            ((_bitMask & 1) == 0 && Math.Abs(d) >= _limitsHz[0])
            || ((_bitMask & 1) == 0 && _linesSinceBaseline >= 16 && Math.Abs(d) >= _limitsHz[1])
            || ((_bitMask & 1) == 0 && _linesSinceBaseline >= 32 && Math.Abs(d) >= _limitsHz[2])
            || ((_bitMask & 2) == 0 && _linesSinceBaseline >= _thresholdLinePositions[0] && Math.Abs(d) >= _limitsHz[3])
            || ((_bitMask & 4) == 0 && _linesSinceBaseline >= _thresholdLinePositions[1] && Math.Abs(d) >= _limitsHz[4])
            || ((_bitMask & 8) == 0 && _linesSinceBaseline >= _thresholdLinePositions[2] && Math.Abs(d) >= _limitsHz[5])
            || ((_bitMask & 16) == 0 && _linesSinceBaseline >= _thresholdLinePositions[3] && Math.Abs(d) >= _limitsHz[6]);

        if (!triggered)
        {
            return null;
        }

        if (_linesSinceBaseline >= 32)
        {
            _bitMask |= 1;
        }

        if (_linesSinceBaseline >= _thresholdLinePositions[0])
        {
            _bitMask |= 2;
        }

        if (_linesSinceBaseline >= _thresholdLinePositions[1])
        {
            _bitMask |= 4;
        }

        if (_linesSinceBaseline >= _thresholdLinePositions[2])
        {
            _bitMask |= 8;
        }

        if (_linesSinceBaseline >= _thresholdLinePositions[3])
        {
            _bitMask |= 16;
        }

        var clampedRate = Math.Min(candidateSampleRate, _sampleRate * 1100.0 / 1060.0); // Main.cpp:4012-4014 -- clamp uses the FIXED rate, not the evolving one (matches legacy's bare `SampFreq` there)
        var correctedRate = NormalSampleRate(clampedRate, 50); // Main.cpp:4015

        // Main.cpp:5586's SSTVSET.SetSampFreq() recomputing m_TW from the just-corrected m_SampFreq,
        // called immediately after every commit (RedrawSampFreq/UpdateSampFreq) -- keeps the next
        // correction's drift formula self-consistent instead of dividing by a stale denominator.
        _currentSampleRate = correctedRate;
        _nominalSamplesPerLine = _lineDurationMs / 1000.0 * correctedRate;

        return correctedRate;
    }

    /// <summary><c>NormalSampFreq</c> (`ComLib.cpp:203-207`) -- rounds to the nearest 1/m fraction.</summary>
    private static double NormalSampleRate(double value, double precision) => (int)(value * precision + 0.5) / precision;
}
