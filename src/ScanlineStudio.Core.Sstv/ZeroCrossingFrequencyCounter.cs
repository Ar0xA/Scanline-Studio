namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// Direct port of legacy <c>CFQC</c> (`sstv.cpp:347-489`) — a zero-crossing-interval frequency
/// discriminator. Used exclusively as AFC's frequency-measurement input
/// (<c>CSSTVDEM::SyncFreq</c>'s <c>m_fqc.Do(m_lvl.m_Cur)</c>, see <see cref="AfcTracker"/>), entirely
/// separate from the PLL-based main demodulator (<see cref="PllFmDemodulator"/>/<c>CPLL</c>).
/// Operates on the raw incoming audio sample directly (matching legacy's <c>m_lvl.m_Cur</c>), since
/// zero-crossing interval timing is amplitude-independent — no AGC needed, unlike the PLL path.
///
/// Kept in real Hz throughout rather than legacy's internal <c>(freq-Center)/BWH</c>-then-x16384
/// scale: <see cref="IirFilter.Process"/> is a purely linear biquad cascade, so filtering
/// <c>(freq-Center)/BWH</c> and rescaling afterward is mathematically identical to filtering
/// <c>freq</c> directly — a representational simplification, not a numeric approximation. See
/// <see cref="AfcTracker"/> for where the corresponding real-Hz threshold values come from.
/// </summary>
internal sealed class ZeroCrossingFrequencyCounter
{
    private readonly IirFilter _outputFilter = new();
    private readonly double _halfSampleRate;
    private double _centerFrequencyHz;
    private double _highClampHz;
    private double _lowClampHz;

    // sstv.h:397 -- ZEROFQ is a FIXED normalized (freq-Center)/BWH value, not recomputed per width.
    // Legacy stores m_fq normalized specifically so a stale reset value stays meaningful in Hz
    // regardless of a LATER width change (denormalized at read time using whatever center/BWH is
    // then-current) -- this port works in real Hz throughout instead, so SetWidth below recomputes
    // the real-Hz equivalent directly whenever the width changes, rather than storing the normalized
    // form and converting at read time.
    private const double ZeroFqNormalized = -1900.0 / 400.0;

    private double _prevSample;
    private double _fractionalCrossingSampleIndex;
    private double _sampleIndex;
    private double _currentFrequencyHz;
    private double _clearedFrequencyHz;
    private double _bwh;
    private bool _widthInitialized;

    public ZeroCrossingFrequencyCounter(double sampleRate)
    {
        _halfSampleRate = sampleRate * 0.5;
        _outputFilter.Design(900, sampleRate, 3); // CFQC::CalcLPF: m_outFC=900, m_outOrder=3
        SetWidth(isNarrow: false);
    }

    /// <summary><c>CFQC::SetWidth</c> (`sstv.cpp:367-383`) — the wide sanity-clamp bounds (not the
    /// tight AFC acceptance band, which lives in <see cref="AfcTracker"/>) and center frequency
    /// differ for the MN/MC "narrow" family. Also recomputes <see cref="_clearedFrequencyHz"/> --
    /// see that field's own doc comment and <see cref="Clear"/>.
    ///
    /// Round-2 code-review finding, fixed here: legacy stores <c>m_fq</c> NORMALIZED, so ANY width
    /// change re-denormalizes whatever is CURRENTLY HELD (a real measurement, not just a `Clear()`
    /// reset value) through the new center/BWH at the next read -- an earlier version of this method
    /// only recomputed <see cref="_clearedFrequencyHz"/>, leaving a live held estimate frozen at its
    /// old real-Hz value across a mid-stream width flip until the next zero crossing corrected it
    /// (a small, bounded divergence -- at most one half-cycle -- but a real one, not present in
    /// legacy). Rescale <see cref="_currentFrequencyHz"/> the same way, skipped on the very first
    /// call (from the constructor), where there is no prior width to rescale FROM.</summary>
    public void SetWidth(bool isNarrow)
    {
        var newCenter = isNarrow ? 2172.0 : 1900.0; // NARROW_CENTER=(2300+2044)/2 vs. normal 1900
        var newBwh = isNarrow ? 128.0 : 400.0; // NARROW_BWH (sstv.h:445, =NARROW_BW/2=256/2) vs. wide m_BWH (sstv.cpp:375)

        if (_widthInitialized)
        {
            _currentFrequencyHz = newCenter + (_currentFrequencyHz - _centerFrequencyHz) / _bwh * newBwh;
        }

        _centerFrequencyHz = newCenter;
        _highClampHz = 2400.0; // same for both
        _lowClampHz = isNarrow ? 1800.0 : 1000.0; // NARROW_AFCLOW vs. normal
        _bwh = newBwh;
        _clearedFrequencyHz = _centerFrequencyHz + ZeroFqNormalized * _bwh;
        _widthInitialized = true;
    }

    /// <summary>Direct port of <c>CFQC::Clear</c> (`sstv.cpp:385-394`) -- legacy calls
    /// <c>m_fqc.Clear()</c> on every <c>Start()</c>/<c>Stop()</c> (`sstv.cpp:1722,1751,1781`), so a
    /// stale interval estimate from a previous transmission never leaks into the next. Resets the
    /// running frequency estimate and internal zero-crossing counters only -- does NOT touch
    /// <see cref="_outputFilter"/>'s Z-state or the width fields (<see cref="SetWidth"/> is a
    /// separate, independent legacy call). <c>_currentFrequencyHz</c> resets to
    /// <see cref="_clearedFrequencyHz"/> -- this port's real-Hz equivalent of legacy's normalized
    /// <c>ZEROFQ</c> reset value (`sstv.h:397`), which is 0Hz at the wide width but ~1564Hz at the
    /// narrow width (round-1 code-review finding: an earlier version of this method hardcoded 0.0
    /// unconditionally, correct only for the wide case -- legacy's own
    /// <c>CSSTVDEM::Start</c> calls <c>SetWidth</c> BEFORE <c>Clear()</c>, `sstv.cpp:1719,1722`, so
    /// the narrow case is genuinely reachable, not a theoretical corner).</summary>
    public void Clear()
    {
        _prevSample = 0.0;
        _fractionalCrossingSampleIndex = 0.0;
        _sampleIndex = 0.0;
        _currentFrequencyHz = _clearedFrequencyHz;
    }

    /// <summary>Processes one raw audio sample; returns the smoothed frequency estimate in Hz
    /// (sample-and-hold between zero crossings, matching legacy's own <c>m_fq</c> behavior).</summary>
    public double ProcessSample(double input)
    {
        if ((input >= 0 && _prevSample < 0) || (input < 0 && _prevSample >= 0))
        {
            // Linear interpolation of the exact (sub-sample) crossing time, CFQC::Do:
            // offset = d/(d-m_d); m_ACount = m_Count - offset; count = (m_Count-m_ACount_prev) - offset.
            var offset = input / (input - _prevSample);
            var count = _sampleIndex - _fractionalCrossingSampleIndex - offset;
            _fractionalCrossingSampleIndex = _sampleIndex - offset;

            if (count >= 1.0)
            {
                var measuredFrequencyHz = _halfSampleRate / count; // half-period per zero crossing
                _currentFrequencyHz = Math.Clamp(measuredFrequencyHz, _lowClampHz, _highClampHz);
            }
        }

        _prevSample = input;
        _sampleIndex += 1;

        return _outputFilter.Process(_currentFrequencyHz);
    }

    /// <summary>Test-only visibility into the running frequency estimate <see cref="Clear"/> resets
    /// -- production code has no need to read this back (only <see cref="ProcessSample"/>'s smoothed
    /// return value is consumed). Round-1 D0-audit finding: lets a test prove <see cref="Clear"/> was
    /// actually called at a given teardown point, not just that the call compiles.</summary>
    internal double CurrentFrequencyHzForTests => _currentFrequencyHz;
}
