namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// Direct port of legacy <c>CPLL</c> (`sstv.cpp`): a closed-loop FM discriminator — AGC on the
/// input (tracked per half-cycle, reset at zero-crossings) → loop IIR lowpass on the previous
/// phase error → drives a <see cref="Vco"/> → multiplying phase detector produces the next error
/// → an output IIR lowpass is the final demodulated value. Ported exactly (same filter orders/
/// cutoffs, same AGC formula) rather than an invented technique — see CLAUDE.md's "port first,
/// invent second" rule for DSP/codec math.
///
/// No longer this port's main picture-decode demodulator (<c>AnalogFmSstvDecoder</c> uses
/// <see cref="HilbertFmDemodulator"/> for that, matching legacy's real compiled-in default,
/// <c>m_Type=2</c>) — but still genuinely load-bearing, not vestigial: a dedicated instance drives
/// AVT training-lock detection (<see cref="AvtTrainingLockStateMachine"/>), matching legacy's own
/// real behavior of always using PLL there regardless of which demodulator handles the picture
/// stream (`sstv.cpp:2129/2159/2169/2187/2222`, all outside the `m_Type`-dispatched switch).
///
/// Legacy returns <c>outLPF.Do(m_out) * 32768 * vcogain</c> — an MMSSTV-internal scale. This port
/// instead converts the loop's normalized frequency-deviation output back to Hz
/// (<c>centerFrequencyHz - normalizedOutput * bandwidthHz</c>), since that's what this decoder's
/// pixel-mapping needs; the derivation is in the spec/06-sstv-dsp.md parity notes, not yet
/// cross-checked against a captured legacy golden vector.
///
/// Scale bridge (found by the holistic review of the VIS/preamble-lock system, same category as
/// <c>LevelAgc</c>'s own): <c>CPLL::Do</c>'s AGC (<c>sstv.cpp:316-343</c>) resets its own tracking
/// window to <c>m_Max=1.0; m_Min=-1.0</c> every half-cycle -- the *same* literal ±1.0 floor this
/// class uses (<see cref="_max"/>/<see cref="_min"/>). Legacy's real input is int16-scaled, so that
/// floor only ever binds during near-silence; for legacy, a real full-scale signal always exceeds
/// it and the AGC adapts to the signal's *actual* peak-to-peak range. This port's raw samples are
/// float in [-1.0, 1.0] (`spec/05-audio-engine.md:44`), so a full-scale signal sits *exactly* at the
/// floor -- coincidentally converging to the same effective gain as legacy's own full-scale case
/// (both settle on `5.0/2.0 = 2.5`), which is why every fixture in this suite (all full-scale) was
/// unaffected by this gap and it went unnoticed. Below full scale, though, the floor never releases:
/// the AGC stays pinned at 2.5 regardless of actual signal amplitude, so loop drive falls linearly
/// with input level instead of staying normalized -- this port's PLL has effectively no AGC at all
/// for anything quieter than 0dBFS, unlike legacy's. Callers must scale by 32768.0 before calling
/// <see cref="ProcessSample"/>, mirroring the exact bridge <c>LevelAgc</c> already uses (see that
/// class's own doc comment) -- keeping every constant inside this class itself (the ±1.0 floor, the
/// `5.0` target) literally identical to legacy's.
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
