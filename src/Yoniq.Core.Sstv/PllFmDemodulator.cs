namespace Yoniq.Core.Sstv;

/// <summary>
/// Direct port of legacy <c>CPLL</c> (`sstv.cpp`): a closed-loop FM discriminator — AGC on the
/// input (tracked per half-cycle, reset at zero-crossings) → loop IIR lowpass on the previous
/// phase error → drives a <see cref="Vco"/> → multiplying phase detector produces the next error
/// → an output IIR lowpass is the final demodulated value. Ported exactly (same filter orders/
/// cutoffs, same AGC formula) rather than an invented technique — see CLAUDE.md's "port first,
/// invent second" rule for DSP/codec math.
///
/// Legacy returns <c>outLPF.Do(m_out) * 32768 * vcogain</c> — an MMSSTV-internal scale. This port
/// instead converts the loop's normalized frequency-deviation output back to Hz
/// (<c>centerFrequencyHz - normalizedOutput * bandwidthHz</c>), since that's what this decoder's
/// pixel-mapping needs; the derivation is in the spec/06-sstv-dsp.md parity notes, not yet
/// cross-checked against a captured legacy golden vector.
/// </summary>
internal sealed class PllFmDemodulator
{
    private readonly Vco _vco;
    private readonly IirFilter _loopFilter = new();
    private readonly IirFilter _outputFilter = new();
    private readonly double _centerFrequencyHz;
    private readonly double _bandwidthHz;

    private double _err;
    private double _max = 1.0;
    private double _min = -1.0;
    private double _prevInput;
    private double _agc = 1.0;
    private double _agcPrev;

    public PllFmDemodulator(
        double sampleRate,
        double lowFrequencyHz,
        double highFrequencyHz,
        int loopOrder = 1,
        double loopCutoffHz = 1500,
        int outputOrder = 3,
        double outputCutoffHz = 900)
    {
        _centerFrequencyHz = (lowFrequencyHz + highFrequencyHz) / 2.0;
        _bandwidthHz = highFrequencyHz - lowFrequencyHz;

        _vco = new Vco(sampleRate, _centerFrequencyHz);
        _vco.SetGain(-_bandwidthHz);

        _loopFilter.Design(loopCutoffHz, sampleRate, loopOrder);
        _outputFilter.Design(outputCutoffHz, sampleRate, outputOrder);
    }

    /// <summary>Processes one input sample; returns the demodulated instantaneous frequency in Hz.</summary>
    public double ProcessSample(double input)
    {
        if (_max < input)
        {
            _max = input;
        }

        if (_min > input)
        {
            _min = input;
        }

        if (input >= 0 && _prevInput < 0)
        {
            var agcRange = _max - _min;
            var instantAgc = 5.0 / agcRange;
            _agc = (_agcPrev + instantAgc) * 0.5;
            _agcPrev = instantAgc;
            _max = 1.0;
            _min = -1.0;
        }

        _prevInput = input;
        var d = input * _agc;

        var loopOut = Math.Clamp(_loopFilter.Process(_err), -1.5, 1.5);
        var vcoOut = _vco.Process(loopOut);
        _err = vcoOut * d;

        var filteredOut = _outputFilter.Process(loopOut);
        return _centerFrequencyHz - filteredOut * _bandwidthHz;
    }
}
