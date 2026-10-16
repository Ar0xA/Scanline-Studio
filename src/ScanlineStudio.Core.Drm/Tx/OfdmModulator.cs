using System.Numerics;
using ScanlineStudio.Core.Drm.Dsp;
using ScanlineStudio.Core.Drm.Tables;

namespace ScanlineStudio.Core.Drm.Tx;

/// <summary>OFDM modulation, port of COFDMModulation (drmtx/common/OFDM.cpp:39-131): carriers placed around the
/// 6000 Hz virtual IF bin, IFFT, cyclic guard interval, then a −5650 Hz rotation so the DC carrier lands at the
/// 350 Hz carrier offset (CDrmTransmitter.cpp:217). The rotation phase runs on across symbols for the whole
/// transmission. Double precision, as QSSTV. One instance per transmission, not thread-safe.</summary>
internal sealed class OfdmModulator
{
    /// <summary>SetCarOffset(350.0) in CDRMTransmitter::init_base (CDrmTransmitter.cpp:217).</summary>
    public const double CarrierOffsetHz = 350.0;

    private readonly int _fftSize;
    private readonly int _guardSize;
    private readonly int _shiftedKmin;
    private readonly int _endIndex;
    private readonly FftPlan _fft;
    private readonly Complex[] _fftInput;
    private readonly Complex[] _fftOutput;
    private readonly double _stepRe;
    private readonly double _stepIm;
    private double _curRe = 1.0;
    private double _curIm;

    public OfdmModulator(CellMappingTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        _fftSize = table.FftSize;
        _guardSize = table.GuardSize;
        _shiftedKmin = table.ShiftedKmin;
        _endIndex = table.ShiftedKmax + 1;
        NumCarriers = table.NumCarriers;
        SymbolBlockSize = table.SymbolBlockSize;
        _fft = new FftPlan(_fftSize);
        _fftInput = new Complex[_fftSize];
        _fftOutput = new Complex[_fftSize];

        // Same operation order as OFDM.cpp:101-107: ((-2·π)·(offset − VIF)) / fs.
        double norm = -2.0 * 3.14159265358979323846 * (CarrierOffsetHz - DrmGlobal.VirtualIntermediateFrequency) / DrmGlobal.SoundCardSampleRate;
        _stepRe = Math.Cos(norm);
        _stepIm = Math.Sin(norm);
    }

    public int NumCarriers { get; }

    /// <summary>Samples per symbol, guard included.</summary>
    public int SymbolBlockSize { get; }

    /// <summary>Modulates one symbol of <see cref="NumCarriers"/> cells into <see cref="SymbolBlockSize"/> complex samples.</summary>
    public void Modulate(ReadOnlySpan<Complex> cells, Span<Complex> output)
    {
        if (cells.Length != NumCarriers)
            throw new ArgumentException($"Expected {NumCarriers} cells.", nameof(cells));
        if (output.Length != SymbolBlockSize)
            throw new ArgumentException($"Expected {SymbolBlockSize} samples.", nameof(output));

        for (int i = _shiftedKmin; i < _endIndex; i++)
            _fftInput[i] = cells[i - _shiftedKmin];

        // Ifft scales by 1/n (MatlibStdToolbox.cpp:552-556), then OFDM.cpp:59 multiplies by n: not an identity in double.
        _fft.Backward(_fftInput, _fftOutput);
        double scale = 1.0 / _fftSize;
        double n = _fftSize;
        for (int i = 0; i < _fftSize; i++)
        {
            Complex v = _fftOutput[i];
            output[i + _guardSize] = new Complex(v.Real * scale * n, v.Imaginary * scale * n);
        }

        for (int i = 0; i < _guardSize; i++)
            output[i] = output[_fftSize + i];

        // x · conj(cur), then cur *= step, as std::complex<double> multiplies: (ac − bd, ad + bc) (OFDM.cpp:73-85).
        for (int i = 0; i < output.Length; i++)
        {
            double a = output[i].Real, b = output[i].Imaginary;
            double c = _curRe, d = -_curIm;
            output[i] = new Complex((a * c) - (b * d), (a * d) + (b * c));

            double nr = (_curRe * _stepRe) - (_curIm * _stepIm);
            double ni = (_curRe * _stepIm) + (_curIm * _stepRe);
            _curRe = nr;
            _curIm = ni;
        }
    }
}
