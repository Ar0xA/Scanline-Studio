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
        for (var i = 0; i < _tableSize; i++)
        {
            _sinTable[i] = Math.Sin(i * 2.0 * Math.PI / _tableSize);
        }

        SetFreeFrequency(centerFrequencyHz);
    }

    public void SetGain(double gain) => _gainTableUnits = _tableSize * gain / _sampleRate;

    public void SetFreeFrequency(double frequencyHz) => _centerIncrement = _tableSize * frequencyHz / _sampleRate;

    public double Process(double controlInput)
    {
        _phase += controlInput * _gainTableUnits + _centerIncrement;
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
