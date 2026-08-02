using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv;

/// <summary>
/// Direct port of legacy <c>CSYNCINT</c> (`sstv.cpp:1290-1411`, `sstv.h:561-590`) — a peak-interval
/// pattern tracker. Fed amplitude peaks over time (<see cref="Trigger"/>/<see cref="UpdateMax"/>,
/// <c>SyncTrig</c>/<c>SyncMax</c>) and a per-sample counter (<see cref="Increment"/>, <c>SyncInc</c>),
/// it measures the interval between consecutive accepted peaks, and checks whether that interval (or
/// a simple integer subharmonic of it, legacy checks 1/2/3x) matches any candidate mode's own known
/// line duration closely enough, requiring several consecutive matching intervals (a per-mode-group
/// depth, <c>SyncCheckSub</c>) before accepting a match (<see cref="TryStart"/>, <c>SyncStart</c>).
///
/// This is the mechanism behind legacy's three parallel sync-acquisition strategies (`sstv.cpp`'s
/// <c>CSSTVDEM::Do</c>, <c>m_sint1</c>/<c>m_sint2</c>/<c>m_sint3</c>) — recognizing a transmission's
/// mode purely from its sync pulse's own repeating timing, without ever needing to decode a VIS
/// code. Only the class itself is ported here; wiring it into any of the three usage sites (and the
/// separate, smaller subset of matches legacy actually acts on for VIS-bypass detection,
/// `sstv.cpp:1912-1922`) is later, separately-scoped work — this is deliberately the smallest
/// independently-verifiable piece, per user direction to chop large ports into parts that can be
/// checked against legacy and debugged individually.
/// </summary>
internal sealed class SyncIntervalTracker
{
    private const int HistorySize = 8; // MSYNCLINE, sstv.h:560
    private const int SubharmonicLimit = 3; // legacy checks w, w/2, w/3 (sstv.cpp:1342, 1358)

    private readonly double[] _history = new double[HistorySize]; // m_MSyncList
    private readonly double _toleranceSamples; // deff = 3ms in samples (sstv.cpp:1326, 1355)
    private readonly double _minIntervalSamples; // SSTVSET.m_MSL
    private readonly double _maxIntervalSamples; // SSTVSET.m_MSH
    private readonly double _minSeparationSamples; // SSTVSET.m_MSLL
    private readonly bool _isNarrow; // m_fNarrow
    private readonly IReadOnlyList<(SstvModeDefinition Mode, double ExpectedIntervalSamples)> _candidates;

    private double _sampleCounter; // m_MSyncCnt
    private double _peakAmplitude; // m_MSyncIntMax
    private double _peakPosition; // m_MSyncIntPos
    private double _lastAcceptedPosition; // m_MSyncACnt

    /// <summary>The sample position (per this tracker's own <see cref="Increment"/> count, not an
    /// absolute stream index) of the most recent peak used to confirm a match from
    /// <see cref="TryStart"/>. Legacy doesn't separately expose this (it just starts decoding from
    /// "now" the instant a match commits, <c>Start()</c>, `sstv.cpp:1902-1903`/`1917-1918`) — this
    /// port needs it explicitly since its decoder doesn't work in legacy's continuous real-time
    /// style, and has to know where to actually anchor line decoding once a match is found. Valid
    /// immediately after a non-null <see cref="TryStart"/> return, before any further
    /// <see cref="Trigger"/>/<see cref="UpdateMax"/> calls.</summary>
    public double LastPeakPositionSamples => _peakPosition;

    /// <param name="sampleRate">This decoder's sample rate.</param>
    /// <param name="isNarrow">Whether this tracker instance operates in the MN/MC narrow band --
    /// gates which candidate modes <see cref="TryStart"/> will ever match, mirroring
    /// <c>SyncCheckSub</c>'s own <c>m_fNarrow</c> checks.</param>
    /// <param name="candidates">Every mode this tracker should try to recognize, each with its own
    /// expected sync-interval (in samples) and required-match depth -- see
    /// <see cref="SstvModeRegistry.GetSyncIntervalCandidates"/>, which derives the interval from this
    /// port's own already-`GetTiming`-verified <c>LineDurationMs</c> data rather than re-transcribing
    /// legacy's separate <c>m_MS[]</c> table.</param>
    public SyncIntervalTracker(
        double sampleRate,
        bool isNarrow,
        IReadOnlyList<(SstvModeDefinition Mode, double ExpectedIntervalSamples)> candidates)
    {
        _isNarrow = isNarrow;
        _candidates = candidates;
        _toleranceSamples = 3.0 / 1000.0 * sampleRate;
        _minSeparationSamples = 50.0 / 1000.0 * sampleRate; // m_MSLL, sstv.cpp:583
        _minIntervalSamples = 63.0 / 1000.0 * sampleRate; // m_MSL, sstv.cpp:584
        _maxIntervalSamples = 1390.0 * 3 / 1000.0 * sampleRate; // m_MSH, sstv.cpp:585
    }

    /// <summary>Band-1 S2 fix (pre-Phase-2 audit): exposes <see cref="_maxIntervalSamples"/> (m_MSH)
    /// so <c>AnalogFmSstvDecoder</c>'s pre-lock buffer-trim retention window can share it rather than
    /// re-deriving the same `1390*3` formula independently and risking the two silently desyncing --
    /// this is the longest interval <see cref="TryStart"/> can ever match against, i.e. the deepest a
    /// caller anchoring off <see cref="LastPeakPositionSamples"/> might need to look back.</summary>
    public double MaxIntervalSamples => _maxIntervalSamples;

    /// <summary><c>CSYNCINT::Reset</c> (`sstv.cpp:576-582`) -- clears the rolling interval history and
    /// all peak-tracking state. Legacy calls this from <c>Stop()</c> (`sstv.cpp:1782-1784`) at the end
    /// of every image, so a subsequent transmission's periodicity is judged fresh, not against stale
    /// history from before the previous lock. This port's decoder additionally has to track where the
    /// *next* fed sample maps back to an absolute stream position once this resets <see cref="_sampleCounter"/>
    /// to 0 again -- see <c>AnalogFmSstvDecoder.EndOfImage</c>'s origin-tracking fields.</summary>
    public void Reset()
    {
        Array.Clear(_history);
        _sampleCounter = 0;
        _peakAmplitude = 0;
        _peakPosition = 0;
        _lastAcceptedPosition = 0;
    }

    /// <summary><c>SyncInc</c> (`sstv.cpp:1376-1379`) -- advances the running sample counter every
    /// sample, regardless of whether a peak was seen.</summary>
    public void Increment() => _sampleCounter += 1;

    /// <summary><c>SyncTrig</c> (`sstv.cpp:1381-1385`) -- unconditionally marks the current sample as
    /// the new peak candidate (used to seed a fresh peak search, not to compare against a running
    /// max).</summary>
    public void Trigger(double amplitude)
    {
        _peakAmplitude = amplitude;
        _peakPosition = _sampleCounter;
    }

    /// <summary><c>SyncMax</c> (`sstv.cpp:1387-1393`) -- updates the peak candidate only if
    /// <paramref name="amplitude"/> exceeds the current one (a running max within the current
    /// candidate window).</summary>
    public void UpdateMax(double amplitude)
    {
        if (_peakAmplitude < amplitude)
        {
            _peakAmplitude = amplitude;
            _peakPosition = _sampleCounter;
        }
    }

    /// <summary><c>SyncStart</c> (`sstv.cpp:1395-1411`) -- if a new peak has been recorded since the
    /// last call and enough samples have passed since the last accepted interval, records the
    /// interval into the rolling history and checks it against every candidate mode. Returns the
    /// matched mode, or null.</summary>
    public SstvModeDefinition? TryStart()
    {
        if (_peakAmplitude == 0)
        {
            return null;
        }

        SstvModeDefinition? matched = null;

        if (_peakPosition - _lastAcceptedPosition > _minSeparationSamples)
        {
            var interval = _peakPosition - _lastAcceptedPosition;
            Array.Copy(_history, 1, _history, 0, HistorySize - 1);
            _history[HistorySize - 1] = interval;

            if (interval > _minIntervalSamples)
            {
                matched = Check();
            }

            _lastAcceptedPosition = _peakPosition;
        }

        _peakAmplitude = 0;
        return matched;
    }

    /// <summary><c>SyncCheck</c> (`sstv.cpp:1353-1374`) -- checks the most recent interval (and its
    /// 1/2, 1/3 subharmonics) against every candidate's expected interval.</summary>
    private SstvModeDefinition? Check()
    {
        var mostRecent = _history[HistorySize - 1];

        for (var k = 1; k <= SubharmonicLimit; k++)
        {
            var candidateInterval = mostRecent / k;
            if (candidateInterval <= _minIntervalSamples || candidateInterval >= _maxIntervalSamples)
            {
                break; // legacy: once out of the overall valid range, larger k only gets smaller -- stop
            }

            foreach (var (mode, expectedInterval) in _candidates)
            {
                if (expectedInterval <= 0)
                {
                    continue; // e.g. AVT, m_MS[smAVT]=0 -- never a candidate
                }

                if (candidateInterval > expectedInterval - _toleranceSamples && candidateInterval < expectedInterval + _toleranceSamples
                    && CheckConsecutiveHistory(mode))
                {
                    return mode;
                }
            }
        }

        return null;
    }

    /// <summary><c>SyncCheckSub</c> (`sstv.cpp:1290-1351`) -- requires several *consecutive* prior
    /// intervals (a per-mode-group depth, `sstv.cpp:1296-1325`) to also match this same candidate's
    /// expected interval (or its 1/2, 1/3 subharmonics for non-narrow, 1/2 only for narrow) before
    /// accepting -- the confidence mechanism that keeps a single lucky interval from being trusted.</summary>
    private bool CheckConsecutiveHistory(SstvModeDefinition mode)
    {
        var depth = SstvModeRegistry.GetSyncIntervalMatchDepth(mode, _isNarrow);
        if (depth is null)
        {
            return false; // always-excluded (e.g. SC2-120/60) or wrong narrow/normal band for this mode
        }

        var expectedInterval = _candidates.First(c => c.Mode == mode).ExpectedIntervalSamples;
        var subharmonicLimit = _isNarrow ? 2 : 3; // sstv.cpp:1336 vs. 1342

        for (var i = HistorySize - 2; i >= depth.Value; i--)
        {
            var w = _history[i];
            var matchedThisEntry = false;

            if (w > _minIntervalSamples)
            {
                for (var k = 1; k <= subharmonicLimit; k++)
                {
                    var ww = w / k;
                    if (ww > expectedInterval - _toleranceSamples && ww < expectedInterval + _toleranceSamples)
                    {
                        matchedThisEntry = true;
                    }
                }
            }

            if (!matchedThisEntry)
            {
                return false;
            }
        }

        return true;
    }
}
