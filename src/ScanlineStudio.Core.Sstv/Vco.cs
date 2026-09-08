namespace ScanlineStudio.Core.Sstv;

/// <summary>Direct port of legacy <c>CVCO</c> (`sstv.cpp`) — a table-lookup sine oscillator whose
/// phase increment is driven by a control input plus a fixed center-frequency increment.</summary>
internal sealed class Vco
{
    private readonly double[] _sinTable;
    private readonly int _tableSize;
    private readonly double _sampleRate;
    private double _gainTableUnits;
    private double _centerIncrement;
    private double _phase;

    public Vco(double sampleRate, double centerFrequencyHz)
    {
        _sampleRate = sampleRate;
        _tableSize = (int)(sampleRate * 2);
        _sinTable = new double[_tableSize];
        // sstv.cpp:82-85 -- legacy precomputes the per-entry angle increment once, then multiplies
        // by the entry index, rather than recomputing `2*PI/N` inside the loop -- same operation
        // order so table entries are bit-identical, not 1 ULP off from a different FP association.
        var angleIncrement = 2.0 * Math.PI / _tableSize;
        for (var i = 0; i < _tableSize; i++)
        {
            _sinTable[i] = Math.Sin(i * angleIncrement);
        }

        // sstv.cpp:79 -- CVCO's own ctor default (m_c1 = m_TableSize/16.0). Every real caller
        // (this port's PllFmDemodulator, legacy's CPLL/CSSTVMOD) calls SetGain before Process, so
        // this default is unobservable today -- kept for defense-in-depth against a future caller
        // that forgets to, matching legacy rather than silently defaulting to a fixed-frequency
        // (zero-gain) oscillator.
        _gainTableUnits = _tableSize / 16.0;
        SetFreeFrequency(centerFrequencyHz);
    }

    public void SetGain(double gain) => _gainTableUnits = _tableSize * gain / _sampleRate;

    public void SetFreeFrequency(double frequencyHz) => _centerIncrement = _tableSize * frequencyHz / _sampleRate;

    public double Process(double controlInput)
    {
        _phase += controlInput * _gainTableUnits + _centerIncrement;
        // Retain legacy addition/subtraction order on ordinary phases. A corrupt extreme input
        // otherwise makes these loops either enormous or unable to advance in double precision.
        if (!double.IsFinite(_phase)) _phase = 0;
        else if (Math.Abs(_phase) > 4.0 * _tableSize) _phase %= _tableSize;
        while (_phase >= _tableSize)
        {
            _phase -= _tableSize;
        }

        while (_phase < 0)
        {
            _phase += _tableSize;
        }

        return _sinTable[(int)_phase];
    }
}
