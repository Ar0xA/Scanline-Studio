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

    private double _prevSample;
    private double _fractionalCrossingSampleIndex;
    private double _sampleIndex;
    private double _currentFrequencyHz;

    public ZeroCrossingFrequencyCounter(double sampleRate)
    {
        _halfSampleRate = sampleRate * 0.5;
        _outputFilter.Design(900, sampleRate, 3); // CFQC::CalcLPF: m_outFC=900, m_outOrder=3
        SetWidth(isNarrow: false);
    }

    /// <summary><c>CFQC::SetWidth</c> (`sstv.cpp:367-383`) — the wide sanity-clamp bounds (not the
    /// tight AFC acceptance band, which lives in <see cref="AfcTracker"/>) and center frequency
    /// differ for the MN/MC "narrow" family.</summary>
    public void SetWidth(bool isNarrow)
    {
        _centerFrequencyHz = isNarrow ? 2172.0 : 1900.0; // NARROW_CENTER=(2300+2044)/2 vs. normal 1900
        _highClampHz = 2400.0; // same for both
        _lowClampHz = isNarrow ? 1800.0 : 1000.0; // NARROW_AFCLOW vs. normal
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
}
